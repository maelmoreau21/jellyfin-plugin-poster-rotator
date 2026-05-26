using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.PosterRotator.Helpers;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PosterRotator;

public class PosterRotatorService : IPosterRotatorService
{
    private const string LegacyPoolDirectoryName = ".poster_pool";
    private const int MaxRemoteImageRedirects = 5;
    private static readonly TimeSpan DiagnosticsCacheDuration = TimeSpan.FromSeconds(15);
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private readonly SemaphoreSlim _diagnosticsLock = new(1, 1);
    private readonly ILibraryManager _library;
    private readonly IProviderManager _providerManager;
    private readonly IHttpClientFactory _httpFactory;
    private readonly PoolStore _poolStore;
    private readonly IImageProcessor _imageProcessor;
    private readonly ILogger<PosterRotatorService> _log;
    private PoolDiagnostics? _cachedDiagnostics;
    private DateTimeOffset _cachedDiagnosticsUtc;

    private enum PoolProcessingMode
    {
        RotateOnly,
        DownloadOnly
    }

    public PosterRotatorService(
        ILibraryManager library,
        IProviderManager providerManager,
        IHttpClientFactory httpFactory,
        PoolStore poolStore,
        IImageProcessor imageProcessor,
        ILogger<PosterRotatorService> log)
    {
        _library = library;
        _providerManager = providerManager;
        _httpFactory = httpFactory;
        _poolStore = poolStore;
        _imageProcessor = imageProcessor;
        _log = log;
    }

    public Task RunAsync(Configuration cfg, IProgress<double>? progress, CancellationToken ct) =>
        RunRotationAsync(cfg, progress, ct);

    public async Task RunRotationAsync(Configuration cfg, IProgress<double>? progress, CancellationToken ct)
    {
        await _operationLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var indexBatch = await _poolStore.BeginDeferredIndexWritesAsync(ct).ConfigureAwait(false);
            await RunRotationInternalAsync(cfg, progress, ct).ConfigureAwait(false);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task DownloadMissingPoolsAsync(Configuration cfg, IProgress<double>? progress, CancellationToken ct)
    {
        await DownloadMissingPoolsWithResultAsync(cfg, progress, ct).ConfigureAwait(false);
    }

    public async Task<PoolDownloadResult> DownloadMissingPoolsNowAsync(CancellationToken ct)
    {
        var cfg = Plugin.Instance?.Configuration ?? new Configuration();
        return await DownloadMissingPoolsWithResultAsync(cfg, null, ct).ConfigureAwait(false);
    }

    private async Task<PoolDownloadResult> DownloadMissingPoolsWithResultAsync(
        Configuration cfg,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        await _operationLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var indexBatch = await _poolStore.BeginDeferredIndexWritesAsync(ct).ConfigureAwait(false);
            return await DownloadMissingPoolsInternalAsync(cfg, progress, ct).ConfigureAwait(false);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    private (Dictionary<string, List<string>> LibraryMap, SelectedRoots Selection, List<BaseItemKind> Kinds) ResolveRunScope(Configuration cfg)
    {
        var kinds = GetRotationKinds(cfg);
        var libraryMap = GetLibraryRootPaths();
        var allLibraryRoots = libraryMap
            .SelectMany(kv => kv.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        // ManualLibraryRoots is retained only for config compatibility; runtime selection now uses UI libraries or all roots.
        if (cfg.ManualLibraryRoots != null && cfg.ManualLibraryRoots.Count > 0)
        {
            _log.LogInformation("PosterRotator: ignoring legacy ManualLibraryRoots during this run.");
            cfg.ManualLibraryRoots.Clear();
        }

        var configuredNames = GetConfiguredLibraryNames(cfg);
        var selection = ResolveSelectedRoots(cfg, configuredNames, libraryMap, allLibraryRoots);
        return (libraryMap, selection, kinds);
    }

    private async Task RunRotationInternalAsync(Configuration cfg, IProgress<double>? progress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        int rotatedCount = 0, skippedCount = 0, errorCount = 0;

        var scope = ResolveRunScope(cfg);
        var entries = await _poolStore.GetIndexEntriesAsync(ct).ConfigureAwait(false);
        var candidates = entries
            .Where(entry => entry.ImageCount > 0 && PoolEntryMatchesSelection(entry, scope.Selection, scope.LibraryMap))
            .ToList();
        ShufflePoolEntries(candidates);

        if (candidates.Count == 0)
        {
            _log.LogWarning("PosterRotator: no indexed pools with images found; rotation run has nothing to do.");
            progress?.Report(100);
            return;
        }

        var total = candidates.Count;
        var done = 0;
        var batchSize = NormalizeProcessingBatchSize(cfg.ProcessingBatchSize);
        var budget = new RotationRunBudget(cfg);

        foreach (var batch in candidates.Chunk(batchSize))
        {
            foreach (var entry in batch)
            {
                ct.ThrowIfCancellationRequested();

                if (!budget.HasRotationSlots)
                {
                    skippedCount += Math.Max(0, total - done);
                    progress?.Report(100);
                    break;
                }

                if (!Guid.TryParse(entry.ItemId, out var itemId))
                {
                    skippedCount++;
                    progress?.Report(++done * 100.0 / Math.Max(1, total));
                    continue;
                }

                var item = TryGetItemById(itemId);
                if (item == null)
                {
                    skippedCount++;
                    progress?.Report(++done * 100.0 / Math.Max(1, total));
                    continue;
                }

                try
                {
                    var result = await ProcessItemAsync(
                        item,
                        cfg,
                        ct,
                        scope.LibraryMap,
                        budget,
                        PoolProcessingMode.RotateOnly,
                        forcePluginDataStorage: true).ConfigureAwait(false);
                    if (result.Rotated)
                        rotatedCount++;
                    else
                        skippedCount++;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    errorCount++;
                    _log.LogWarning(ex, "PosterRotator: error rotating \"{Name}\" ({Path})", item.Name, item.Path);
                }

                progress?.Report(++done * 100.0 / Math.Max(1, total));
            }

            if (!budget.HasRotationSlots)
                break;
        }

        QueueLibraryScanIfRequested(cfg, rotatedCount);

        sw.Stop();
        _log.LogInformation(
            "PosterRotator: rotation complete - {Rotated}/{Total} rotated, {Skipped} skipped, {Errors} errors, took {Elapsed:0.0}s",
            rotatedCount,
            total,
            skippedCount,
            errorCount,
            sw.Elapsed.TotalSeconds);
    }

    private async Task<PoolDownloadResult> DownloadMissingPoolsInternalAsync(Configuration cfg, IProgress<double>? progress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        int processedCount = 0, skippedCount = 0, errorCount = 0, topUpCount = 0, completedPoolCount = 0;
        var limitedByRunBudget = false;

        var scope = ResolveRunScope(cfg);
        var hasSelection = scope.Selection.Paths.Count > 0 || scope.Selection.LibraryNames.Count > 0;
        var poolRoot = _poolStore.TryGetPoolRootPath(create: false);
        _log.LogInformation(
            "PosterRotator: missing-pool download using pool root {PoolRoot}",
            string.IsNullOrWhiteSpace(poolRoot) ? "(unavailable)" : poolRoot);

        var query = new InternalItemsQuery
        {
            IncludeItemTypes = scope.Kinds.Distinct().ToArray(),
            Recursive = true
        };
        var topParentIds = GetSelectedLibraryTopParentIds(scope.Selection.LibraryNames);
        if (topParentIds.Count > 0)
            query.TopParentIds = topParentIds.ToArray();

        var itemIds = _library.GetItemIds(query).ToArray();
        ShuffleItemIds(itemIds);
        if (itemIds.Length == 0)
        {
            _log.LogWarning("PosterRotator: no items returned by library manager; aborting run.");
            return new PoolDownloadResult
            {
                CandidateCount = 0,
                Message = "Aucun media candidat."
            };
        }

        var total = itemIds.Length;
        var done = 0;
        var batchSize = NormalizeProcessingBatchSize(cfg.ProcessingBatchSize);
        var poolSize = NormalizePoolSize(cfg.PoolSize);
        var budget = new RotationRunBudget(cfg);
        var indexedCounts = await GetIndexedImageCountsAsync(ct).ConfigureAwait(false);
        var candidateCount = itemIds.Count(itemId => !indexedCounts.TryGetValue(itemId, out var count) || count < poolSize);
        _log.LogInformation(
            "PosterRotator: missing-pool download found {CandidateCount}/{Total} candidate item(s); {CompleteCount} already complete.",
            candidateCount,
            total,
            total - candidateCount);

        foreach (var batch in itemIds.Chunk(batchSize))
        {
            foreach (var itemId in batch)
            {
                ct.ThrowIfCancellationRequested();

                if (!budget.HasDownloadWorkRemaining)
                {
                    limitedByRunBudget = true;
                    skippedCount += Math.Max(0, total - done);
                    progress?.Report(100);
                    break;
                }

                if (indexedCounts.TryGetValue(itemId, out var indexedCount) && indexedCount >= poolSize)
                {
                    skippedCount++;
                    progress?.Report(++done * 100.0 / Math.Max(1, total));
                    continue;
                }

                var item = TryGetItemById(itemId);
                if (item == null)
                {
                    skippedCount++;
                    progress?.Report(++done * 100.0 / Math.Max(1, total));
                    continue;
                }

                if (hasSelection && !MatchesSelection(item.Path ?? string.Empty, scope.Selection, scope.LibraryMap))
                {
                    skippedCount++;
                    progress?.Report(++done * 100.0 / Math.Max(1, total));
                    continue;
                }

                try
                {
                    var result = await ProcessItemAsync(
                        item,
                        cfg,
                        ct,
                        scope.LibraryMap,
                        budget,
                        PoolProcessingMode.DownloadOnly,
                        forcePluginDataStorage: true).ConfigureAwait(false);
                    processedCount++;
                    topUpCount += result.TopUps;
                    if (result.TopUps > 0 && result.ImageCount >= poolSize)
                        completedPoolCount++;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    errorCount++;
                    _log.LogWarning(ex, "PosterRotator: error downloading pool images for \"{Name}\" ({Path})", item.Name, item.Path);
                }

                progress?.Report(++done * 100.0 / Math.Max(1, total));
            }

            if (!budget.HasDownloadWorkRemaining)
                break;
        }

        sw.Stop();
        _log.LogInformation(
            "PosterRotator: missing-pool download complete - {Processed}/{Total} processed, {Completed} pool(s) completed, {TopUp} image(s) added, {Skipped} skipped, {Errors} errors, {ProviderLookups} provider lookup(s), {Downloads} download attempt(s), took {Elapsed:0.0}s",
            processedCount,
            total,
            completedPoolCount,
            topUpCount,
            skippedCount,
            errorCount,
            budget.ProviderLookups,
            budget.Downloads,
            sw.Elapsed.TotalSeconds);

        var message = $"{completedPoolCount} pool(s) completees, {topUpCount} image(s) ajoutees.";
        if (limitedByRunBudget)
        {
            message += " Passage limite par les plafonds du run; relancez Telecharger les pools manquants pour continuer.";
        }

        return new PoolDownloadResult
        {
            CandidateCount = candidateCount,
            ProcessedCount = processedCount,
            SkippedCount = skippedCount,
            CompletedPoolCount = completedPoolCount,
            ImagesAdded = topUpCount,
            ErrorCount = errorCount,
            ProviderLookups = budget.ProviderLookups,
            DownloadAttempts = budget.Downloads,
            LimitedByRunBudget = limitedByRunBudget,
            Message = message
        };
    }

    private static List<BaseItemKind> GetRotationKinds(Configuration cfg)
    {
        var kinds = new List<BaseItemKind>
        {
            BaseItemKind.Movie,
            BaseItemKind.Series,
            BaseItemKind.BoxSet
        };

        if (cfg.EnableSeasonPosters) kinds.Add(BaseItemKind.Season);
        if (cfg.EnableEpisodePosters) kinds.Add(BaseItemKind.Episode);
        return kinds;
    }

    private static List<string> GetConfiguredLibraryNames(Configuration cfg)
    {
        var configuredNames = new List<string>();
        if (cfg.LibraryRules is { Count: > 0 })
            configuredNames.AddRange(cfg.LibraryRules.Where(rule => rule.Enabled).Select(rule => rule.Name));
        else if (cfg.Libraries is { Count: > 0 })
            configuredNames.AddRange(cfg.Libraries);

        return configuredNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<Dictionary<Guid, int>> GetIndexedImageCountsAsync(CancellationToken cancellationToken)
    {
        var entries = await _poolStore.GetIndexEntriesAsync(cancellationToken).ConfigureAwait(false);
        var result = new Dictionary<Guid, int>();
        foreach (var entry in entries)
        {
            if (Guid.TryParse(entry.ItemId, out var itemId))
                result[itemId] = Math.Max(result.GetValueOrDefault(itemId), entry.ImageCount);
        }

        return result;
    }

    private SelectedRoots ResolveSelectedRoots(
        Configuration cfg,
        List<string> configuredNames,
        Dictionary<string, List<string>> libraryMap,
        List<string> allLibraryRoots)
    {
        var result = new SelectedRoots();

        if (configuredNames.Count > 0)
        {
            foreach (var name in configuredNames)
            {
                if (libraryMap.TryGetValue(name, out var paths) && paths is { Count: > 0 })
                {
                    result.Paths.AddRange(paths);
                    result.LibraryNames.Add(name);
                }
                else
                {
                    _log.LogWarning("PosterRotator: configured library '{Name}' not found among current libraries", name);
                }
            }

            DeduplicatePaths(result.Paths);
            return result;
        }

        result.Paths.AddRange(allLibraryRoots.Distinct(StringComparer.OrdinalIgnoreCase));
        if (result.Paths.Count > 0)
            _log.LogInformation("PosterRotator: no libraries configured, defaulting to all library roots.");

        return result;
    }

    private static bool MatchesSelection(
        string itemPath,
        SelectedRoots selection,
        Dictionary<string, List<string>> libraryMap)
    {
        if (string.IsNullOrWhiteSpace(itemPath))
            return false;

        if (selection.Paths.Any(root => LooksLikePath(root) && PluginHelpers.IsPathInsideOrEqual(itemPath, root)))
            return true;

        foreach (var name in selection.LibraryNames)
        {
            if (!libraryMap.TryGetValue(name, out var roots))
                continue;

            if (roots.Any(root => PluginHelpers.IsPathInsideOrEqual(itemPath, root)))
                return true;
        }

        return false;
    }

    private static bool PoolEntryMatchesSelection(
        PoolIndexEntry entry,
        SelectedRoots selection,
        Dictionary<string, List<string>> libraryMap)
    {
        if (selection.Paths.Count == 0 && selection.LibraryNames.Count == 0)
            return true;

        if (!string.IsNullOrWhiteSpace(entry.LibraryName)
            && selection.LibraryNames.Contains(entry.LibraryName))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(entry.ItemPath)
            && MatchesSelection(entry.ItemPath, selection, libraryMap))
        {
            return true;
        }

        return false;
    }

    private static void DeduplicatePaths(List<string> paths)
    {
        if (paths.Count <= 1) return;
        var unique = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        paths.Clear();
        paths.AddRange(unique);
    }

    private sealed class SelectedRoots
    {
        public List<string> Paths { get; } = new();
        public HashSet<string> LibraryNames { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    internal sealed record RemoteImageDownloadCandidate(RemoteImageInfo Image, string? Language);

    internal sealed class RotationRunBudget
    {
        private readonly int _maxRotations;
        private readonly int _maxDownloads;
        private readonly int _maxProviderLookups;

        public RotationRunBudget(Configuration cfg)
        {
            _maxRotations = NormalizeRotationRunLimit(cfg.MaxRotationsPerRun, 500);
            _maxDownloads = NormalizeRunLimit(cfg.MaxDownloadsPerRun, 250);
            _maxProviderLookups = NormalizeRunLimit(cfg.MaxProviderLookupsPerRun, 250);
        }

        public int Rotations { get; private set; }
        public int Downloads { get; private set; }
        public int ProviderLookups { get; private set; }

        public bool HasRotationSlots => Rotations < _maxRotations;
        public bool HasDownloadSlots => Downloads < _maxDownloads;
        public bool HasProviderLookupSlots => ProviderLookups < _maxProviderLookups;
        public bool HasWorkRemaining => HasRotationSlots || HasProviderLookupSlots;
        public bool HasDownloadWorkRemaining => HasDownloadSlots && HasProviderLookupSlots;

        public void RecordRotation()
        {
            if (HasRotationSlots)
                Rotations++;
        }

        public bool TryUseDownloadSlot()
        {
            if (!HasDownloadSlots)
                return false;

            Downloads++;
            return true;
        }

        public bool TryUseProviderLookupSlot()
        {
            if (!HasProviderLookupSlots)
                return false;

            ProviderLookups++;
            return true;
        }
    }

    private static bool LooksLikePath(string entry) =>
        entry.IndexOf(':') >= 0 || entry.IndexOf('\\') >= 0 || entry.IndexOf('/') >= 0;

    internal static int NormalizePoolSize(int value) =>
        Math.Clamp(value <= 0 ? 4 : value, 1, 50);

    internal static int NormalizeMinHours(int value) =>
        Math.Clamp(value <= 0 ? 72 : value, 1, 24 * 365);

    internal static int NormalizeProcessingBatchSize(int value) =>
        Math.Clamp(value <= 0 ? 250 : value, 10, 5000);

    internal static int NormalizeRunLimit(int value, int fallback) =>
        Math.Clamp(value <= 0 ? fallback : value, 1, 100000);

    internal static int NormalizeRotationRunLimit(int value, int fallback) =>
        value == 0 ? int.MaxValue : NormalizeRunLimit(value, fallback);

    internal static int NormalizePreferredLanguageLimit(int value) =>
        Math.Clamp(value < 0 ? 0 : value, 0, 50);

    internal static bool IsRotationDue(DateTimeOffset? lastRotatedUtc, DateTimeOffset now, int minHoursBetweenSwitches) =>
        !lastRotatedUtc.HasValue || now - lastRotatedUtc.Value >= TimeSpan.FromHours(NormalizeMinHours(minHoursBetweenSwitches));

    internal static void ShuffleItemIds(Guid[] itemIds)
    {
        for (var i = itemIds.Length - 1; i > 0; i--)
        {
            var j = Random.Shared.Next(i + 1);
            (itemIds[i], itemIds[j]) = (itemIds[j], itemIds[i]);
        }
    }

    private static void ShufflePoolEntries(List<PoolIndexEntry> entries)
    {
        for (var i = entries.Count - 1; i > 0; i--)
        {
            var j = Random.Shared.Next(i + 1);
            (entries[i], entries[j]) = (entries[j], entries[i]);
        }
    }

    private PoolItemSnapshot CreateSnapshot(BaseItem item, Dictionary<string, List<string>> libraryMap)
    {
        return new PoolItemSnapshot(
            item.Id,
            item.Name ?? string.Empty,
            item.GetType().Name,
            ResolveLibraryName(item.Path, libraryMap),
            item.Path);
    }

    private static string ResolveLibraryName(string? itemPath, Dictionary<string, List<string>> libraryMap)
    {
        if (string.IsNullOrWhiteSpace(itemPath))
            return string.Empty;

        foreach (var library in libraryMap)
        {
            if (library.Value.Any(root => PluginHelpers.IsPathInsideOrEqual(itemPath, root)))
                return library.Key;
        }

        return string.Empty;
    }

    private static RotationState CreateRotationState(BaseItem item, PoolMetadata? metadata, string statePath)
    {
        if (metadata == null)
            return PluginHelpers.LoadRotationState(statePath);

        var key = item.Id.ToString();
        var state = new RotationState();
        state.LastIndexByItem[key] = metadata.LastIndex;
        if (metadata.LastRotatedUtc.HasValue)
            state.LastRotatedUtcByItem[key] = metadata.LastRotatedUtc.Value.ToUnixTimeSeconds();

        return state;
    }

    private async Task RecordExistingImageAsync(
        PoolItemSnapshot item,
        string poolDir,
        string path,
        string source,
        string? language,
        CancellationToken cancellationToken)
    {
        if (!PluginHelpers.TryGetImageFormat(path, out _, out var mimeType))
            return;

        var (width, height) = PluginHelpers.GetImageDimensions(path);
        var hash = await ImageHash.ComputeNormalizedHashAsync(path, _imageProcessor, cancellationToken).ConfigureAwait(false);
        await _poolStore.RecordImageAsync(
            item,
            poolDir,
            path,
            source,
            language ?? "unknown",
            sourceUrl: null,
            mimeType,
            width,
            height,
            hash,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<(bool Rotated, int TopUps, int ImageCount)> ProcessItemAsync(
        BaseItem item,
        Configuration cfg,
        CancellationToken ct,
        Dictionary<string, List<string>> libraryMap,
        RotationRunBudget budget,
        PoolProcessingMode mode,
        bool forcePluginDataStorage = false)
    {
        var itemDir = PluginHelpers.GetItemDirectory(item.Path) ?? string.Empty;
        var storageMode = forcePluginDataStorage ? PoolStorageMode.PluginData : ResolveStorageMode(cfg);

        if (storageMode == PoolStorageMode.MediaFolders && (string.IsNullOrEmpty(itemDir) || !Directory.Exists(itemDir)))
            return (false, 0, 0);

        var legacyPoolDir = string.IsNullOrEmpty(itemDir) ? null : Path.Combine(itemDir, LegacyPoolDirectoryName);
        var poolDir = ResolvePoolDirectory(item, storageMode, legacyPoolDir);
        if (string.IsNullOrEmpty(poolDir))
            return (false, 0, 0);

        IDisposable? poolLock = null;
        if (storageMode == PoolStorageMode.PluginData)
            poolLock = await _poolStore.LockPoolAsync(item.Id, ct).ConfigureAwait(false);

        try
        {
            var snapshot = CreateSnapshot(item, libraryMap);
            var hasPoolDirectory = Directory.Exists(poolDir);
            if (storageMode == PoolStorageMode.MediaFolders)
            {
                Directory.CreateDirectory(poolDir);
                hasPoolDirectory = true;
            }

            var local = hasPoolDirectory ? LoadLocalPoolFiles(poolDir) : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            PoolMetadata? metadata = null;
            if (storageMode == PoolStorageMode.PluginData
                && hasPoolDirectory
                && (local.Count > 0 || File.Exists(Path.Combine(poolDir, "pool.json"))))
            {
                metadata = await _poolStore.EnsurePoolAsync(snapshot, poolDir, ct).ConfigureAwait(false);
            }

            var lockFile = Path.Combine(poolDir, "pool.lock");
            var poolIsLocked = File.Exists(lockFile);
            var statePath = Path.Combine(poolDir, "rotation_state.json");
            var state = CreateRotationState(item, metadata, statePath);
            var key = item.Id.ToString();
            var now = DateTimeOffset.UtcNow;
            var minHours = NormalizeMinHours(cfg.MinHoursBetweenSwitches);
            var poolSize = NormalizePoolSize(cfg.PoolSize);
            var haveLast = metadata?.LastRotatedUtc != null || state.LastRotatedUtcByItem.TryGetValue(key, out _);
            var lastRotated = metadata?.LastRotatedUtc
                ?? (state.LastRotatedUtcByItem.TryGetValue(key, out var lastEpoch)
                    ? DateTimeOffset.FromUnixTimeSeconds(lastEpoch)
                    : null);
            var rotationDue = IsRotationDue(lastRotated, now, minHours);
            var allowTopUp = mode == PoolProcessingMode.DownloadOnly && budget.HasDownloadWorkRemaining;

            if (_log.IsEnabled(LogLevel.Debug))
            {
                _log.LogDebug(
                    "PosterRotator: \"{Item}\" pool has {Count}/{Target}. Storage:{Storage}. Locked:{Locked}. Due:{Due}. AllowTopUp:{Allow}",
                    item.Name,
                    local.Count,
                    poolSize,
                    storageMode,
                    poolIsLocked,
                    rotationDue,
                    allowTopUp);
            }

            var topUpCount = 0;
            if (!poolIsLocked && local.Count < poolSize && allowTopUp)
            {
                var createdPoolForTopUp = !Directory.Exists(poolDir);
                if (createdPoolForTopUp && storageMode == PoolStorageMode.MediaFolders)
                    Directory.CreateDirectory(poolDir);

                var added = await TryTopUpFromProvidersAsync(
                    item,
                    poolDir,
                    poolSize - local.Count,
                    cfg,
                    ct,
                    storageMode == PoolStorageMode.PluginData ? snapshot : null,
                    budget).ConfigureAwait(false);
                topUpCount = added.Count;
                foreach (var file in added)
                    local.Add(file);

                if (createdPoolForTopUp && added.Count == 0 && IsDirectoryEmpty(poolDir))
                {
                    TryDeleteDirectory(poolDir);
                }
                else if (metadata == null && storageMode == PoolStorageMode.PluginData && Directory.Exists(poolDir))
                {
                    metadata = await _poolStore.GetPoolAsync(item.Id, reconcileFiles: true, ct).ConfigureAwait(false);
                }

                if (cfg.LockImagesAfterFill && local.Count >= poolSize)
                {
                    Directory.CreateDirectory(poolDir);
                    TryWriteText(lockFile, "locked");
                    poolIsLocked = true;
                    _log.LogInformation("PosterRotator: locked pool for \"{Item}\" at size {Size}.", item.Name, local.Count);
                }
            }
            else if (poolIsLocked && !cfg.LockImagesAfterFill)
            {
                TryDeleteFile(lockFile);
                poolIsLocked = false;
                _log.LogInformation("PosterRotator: unlocked pool for \"{Item}\" (config changed).", item.Name);
            }

            if (mode == PoolProcessingMode.DownloadOnly)
                return (false, topUpCount, local.Count);

            if (local.Count == 0)
                return (false, topUpCount, local.Count);

            if (!rotationDue || !budget.HasRotationSlots)
                return (false, topUpCount, local.Count);

            var chosen = PickNextFor(local.ToList(), item, cfg, state);
            if (!await SavePrimaryImageAsync(item, chosen, ct).ConfigureAwait(false))
                return (false, topUpCount, local.Count);

            budget.RecordRotation();
            state.LastRotatedUtcByItem[key] = now.ToUnixTimeSeconds();
            if (storageMode == PoolStorageMode.PluginData)
                await _poolStore.RecordRotationAsync(snapshot, poolDir, chosen, state.LastIndexByItem.GetValueOrDefault(key), now, ct).ConfigureAwait(false);
            else
                PluginHelpers.SaveRotationState(statePath, state);

            _log.LogInformation("PosterRotator: rotated \"{Item}\" -> {Poster}", item.Name, Path.GetFileName(chosen));
            return (true, topUpCount, local.Count);
        }
        finally
        {
            poolLock?.Dispose();
        }
    }

    private PoolStorageMode ResolveStorageMode(Configuration cfg)
    {
        if (cfg.PoolStorageMode == PoolStorageMode.MediaFolders)
            return PoolStorageMode.MediaFolders;

        var dataFolder = Plugin.Instance?.DataFolderPath;
        if (!string.IsNullOrWhiteSpace(dataFolder))
            return PoolStorageMode.PluginData;

        _log.LogWarning("PosterRotator: plugin data folder is unavailable; falling back to media-folder pools.");
        return PoolStorageMode.MediaFolders;
    }

    private string? ResolvePoolDirectory(BaseItem item, PoolStorageMode mode, string? legacyPoolDir)
    {
        if (mode == PoolStorageMode.MediaFolders)
            return legacyPoolDir;

        return _poolStore.TryGetPoolDirectory(item.Id, create: false);
    }

    private static HashSet<string> LoadLocalPoolFiles(string poolDir)
    {
        var local = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pattern in GetPoolPatterns())
        {
            foreach (var file in Directory.GetFiles(poolDir, pattern))
                local.Add(file);
        }

        return local;
    }

    private async Task<List<string>> TryTopUpFromProvidersAsync(
        BaseItem item,
        string poolDir,
        int needed,
        Configuration cfg,
        CancellationToken ct,
        PoolItemSnapshot? poolItem,
        RotationRunBudget budget)
    {
        var added = new List<string>();
        if (needed <= 0 || !budget.HasDownloadSlots || !budget.TryUseProviderLookupSlot())
            return added;

        var urlMapPath = Path.Combine(poolDir, "pool_urls.json");
        var usePoolStore = poolItem != null;
        var poolExists = Directory.Exists(poolDir);
        var urlMap = usePoolStore ? new Dictionary<string, string>() : PluginHelpers.LoadJsonMap(urlMapPath);
        var knownUrls = usePoolStore && poolExists
            ? new HashSet<string>(await _poolStore.GetKnownSourceUrlsAsync(poolItem!, poolDir, ct).ConfigureAwait(false), StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(urlMap.Values, StringComparer.OrdinalIgnoreCase);
        var knownHashes = usePoolStore && poolExists
            ? new HashSet<ulong>(await _poolStore.GetImageHashesAsync(poolItem!, poolDir, ct).ConfigureAwait(false))
            : new HashSet<ulong>(ImageHash.LoadHashes(poolDir).Values);
        if (cfg.EnableDuplicateDetection && poolExists)
        {
            foreach (var existingFile in LoadLocalPoolFiles(poolDir))
            {
                var normalizedHash = await ImageHash.ComputeNormalizedHashAsync(existingFile, _imageProcessor, ct).ConfigureAwait(false);
                if (normalizedHash != 0)
                    knownHashes.Add(normalizedHash);
            }
        }

        try
        {
            var query = new RemoteImageQuery(string.Empty)
            {
                IncludeAllLanguages = true,
                IncludeDisabledProviders = false
            };

            var images = await PluginHelpers.RetryAsync(
                () => _providerManager.GetAvailableRemoteImages(item, query, ct),
                maxRetries: 3,
                _log,
                ct).ConfigureAwait(false);

            await Harvest(images).ConfigureAwait(false);

            _log.LogInformation("PosterRotator: providers added {Count} image(s) for \"{Item}\"", added.Count, item.Name);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "PosterRotator: provider top-up failed for {Name}", item.Name);
        }

        return added;

        async Task Harvest(IEnumerable<RemoteImageInfo>? images)
        {
            if (images == null)
                return;

            var imageList = OrderRemoteImagesForDownload(images).ToList();
            if (imageList.Count == 0)
                return;

            if (cfg.EnableLanguageFilter)
            {
                var preferredLanguage = NormalizeLanguageCode(cfg.PreferredLanguage) ?? "fr";
                var remainingPreferredSlots = Math.Max(
                    0,
                    NormalizePreferredLanguageLimit(cfg.MaxPreferredLanguageImages)
                    - await CountLanguageImagesInPool(poolDir, preferredLanguage).ConfigureAwait(false));

                foreach (var candidate in SelectRemoteImagesForLanguage(
                    imageList,
                    cfg,
                    GetOriginalLanguage(item),
                    remainingPreferredSlots))
                {
                    if (added.Count >= needed) return;
                    await TryDownloadRemote(candidate.Image, item, poolDir, candidate.Language, knownUrls, urlMapPath).ConfigureAwait(false);
                }
            }
            else
            {
                foreach (var info in imageList.Where(info => info.Type is ImageType.Primary or ImageType.Thumb or ImageType.Backdrop))
                {
                    if (added.Count >= needed) return;
                    await TryDownloadRemote(info, item, poolDir, null, knownUrls, urlMapPath).ConfigureAwait(false);
                }
            }
        }

        async Task<int> CountLanguageImagesInPool(string dir, string language)
        {
            if (string.IsNullOrWhiteSpace(language) || !Directory.Exists(dir))
                return 0;

            if (usePoolStore)
                return await _poolStore.CountLanguageImagesAsync(poolItem!, dir, language, ct).ConfigureAwait(false);

            var metaPath = Path.Combine(dir, "pool_languages.json");
            return PluginHelpers.CountInJsonMap(metaPath, kv => kv.Value.Equals(language, StringComparison.OrdinalIgnoreCase));
        }

        async Task TryDownloadRemote(
            RemoteImageInfo info,
            BaseItem mediaItem,
            string dir,
            string? language,
            HashSet<string> knownUrls,
            string urlMapPath)
        {
            if (info == null || IsLandscape(info))
                return;

            var url = info.Url;
            if (string.IsNullOrWhiteSpace(url) || knownUrls.Contains(url))
                return;

            if (info.Width > 0 && info.Height > 0
                && (info.Width < cfg.MinImageWidth || info.Height < cfg.MinImageHeight))
            {
                _log.LogDebug(
                    "PosterRotator: skipping {Url} - too small ({Width}x{Height}, min {MinWidth}x{MinHeight})",
                    url,
                    info.Width,
                    info.Height,
                    cfg.MinImageWidth,
                    cfg.MinImageHeight);
                return;
            }

            if (!await IsAllowedRemoteImageUrlAsync(url, cfg, ct).ConfigureAwait(false))
            {
                _log.LogWarning("PosterRotator: rejected unsafe remote image URL for {Item}: {Url}", mediaItem.Name, url);
                return;
            }

            if (!budget.TryUseDownloadSlot())
                return;

            var maxBytes = GetMaxDownloadBytes(cfg);
            var baseName = $"pool_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}_{Guid.NewGuid():N}";
            var tmpPath = string.Empty;
            string? finalPath = null;
            var createdDirectory = false;

            try
            {
                if (usePoolStore)
                {
                    var existedBefore = Directory.Exists(dir);
                    var writableDir = _poolStore.TryCreatePoolDirectoryForWrite(poolItem!.ItemId);
                    if (string.IsNullOrWhiteSpace(writableDir))
                    {
                        _log.LogWarning("PosterRotator: unable to create PluginData pool directory for {Item}.", mediaItem.Name);
                        return;
                    }

                    dir = writableDir;
                    createdDirectory = !existedBefore;
                }
                else if (!Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                    createdDirectory = true;
                }

                tmpPath = Path.Combine(dir, baseName + ".tmp");

                using var client = _httpFactory.CreateClient("PosterRotator");
                using var response = await PluginHelpers.RetryAsync(
                    async () =>
                    {
                        var resp = await SendRemoteImageRequestAsync(client, url, cfg, ct).ConfigureAwait(false);
                        if ((int)resp.StatusCode == 429 || (int)resp.StatusCode >= 500)
                        {
                            resp.Dispose();
                            throw new HttpRequestException($"Transient HTTP status {(int)resp.StatusCode}");
                        }

                        return resp;
                    },
                    maxRetries: 3,
                    _log,
                    ct).ConfigureAwait(false);

                response.EnsureSuccessStatusCode();
                var contentLength = response.Content.Headers.ContentLength;
                if (contentLength.HasValue && contentLength.Value > maxBytes)
                {
                    _log.LogInformation(
                        "PosterRotator: rejected remote image for {Item} - Content-Length {Length} exceeds {Limit} bytes.",
                        mediaItem.Name,
                        contentLength.Value,
                        maxBytes);
                    return;
                }

                await using (var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
                {
                    await PluginHelpers.CopyToFileWithLimitAsync(stream, tmpPath, maxBytes, ct).ConfigureAwait(false);
                }

                if (!PluginHelpers.TryGetImageFormat(tmpPath, out var extension, out var mimeType))
                {
                    _log.LogInformation("PosterRotator: rejected remote image for {Item} - unsupported image header.", mediaItem.Name);
                    TryDeleteFile(tmpPath);
                    return;
                }

                var (width, height) = PluginHelpers.GetImageDimensions(tmpPath);
                if (width > 0 && height > 0 && (width < cfg.MinImageWidth || height < cfg.MinImageHeight))
                {
                    _log.LogInformation(
                        "PosterRotator: rejected {Name} - too small ({Width}x{Height}, min {MinWidth}x{MinHeight})",
                        baseName,
                        width,
                        height,
                        cfg.MinImageWidth,
                        cfg.MinImageHeight);
                    TryDeleteFile(tmpPath);
                    return;
                }

                finalPath = Path.Combine(dir, baseName + extension);
                File.Move(tmpPath, finalPath);

                ulong hash = 0;
                if (cfg.EnableDuplicateDetection)
                {
                    hash = await ImageHash.ComputeNormalizedHashAsync(finalPath, _imageProcessor, ct).ConfigureAwait(false);
                    if (hash != 0)
                    {
                        if (ImageHash.IsDuplicate(hash, knownHashes))
                        {
                            _log.LogInformation("PosterRotator: rejected {Name} - visually duplicate", Path.GetFileName(finalPath));
                            TryDeleteFile(finalPath);
                            finalPath = null;
                            return;
                        }

                        knownHashes.Add(hash);
                        if (!usePoolStore)
                            ImageHash.SaveHash(dir, Path.GetFileName(finalPath), hash);
                    }
                }

                if (usePoolStore)
                {
                    await _poolStore.RecordImageAsync(
                        poolItem!,
                        dir,
                        finalPath,
                        source: "remote",
                        language ?? info.Language ?? "unknown",
                        url,
                        mimeType,
                        width,
                        height,
                        cfg.EnableDuplicateDetection ? hash : 0,
                        ct).ConfigureAwait(false);
                }
                else
                {
                    PluginHelpers.UpdateJsonMapFile(Path.Combine(dir, "pool_languages.json"), Path.GetFileName(finalPath), language ?? info.Language ?? "unknown");
                    PluginHelpers.UpdateJsonMapFile(urlMapPath, Path.GetFileName(finalPath), url);
                }

                added.Add(finalPath);
                knownUrls.Add(url);

                _log.LogDebug(
                    "PosterRotator: downloaded {Name} ({Width}x{Height}, {MimeType}) for {Item}",
                    Path.GetFileName(finalPath),
                    width > 0 ? width : null,
                    height > 0 ? height : null,
                    mimeType,
                    mediaItem.Name);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                TryDeleteFile(tmpPath);
                if (!string.IsNullOrWhiteSpace(finalPath) && !added.Contains(finalPath))
                    TryDeleteFile(finalPath);
                throw;
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "PosterRotator: download failed for {Url} ({Item})", url, mediaItem.Name);
                TryDeleteFile(tmpPath);
                if (!string.IsNullOrWhiteSpace(finalPath) && !added.Contains(finalPath))
                    TryDeleteFile(finalPath);
            }
            finally
            {
                if (createdDirectory && IsDirectoryEmpty(dir))
                    TryDeleteDirectory(dir);
            }
        }
    }

    private async Task<bool> SavePrimaryImageAsync(BaseItem item, string chosenPath, CancellationToken ct)
    {
        if (!File.Exists(chosenPath))
            return false;

        if (!PluginHelpers.TryGetImageFormat(chosenPath, out _, out var mimeType))
        {
            _log.LogWarning("PosterRotator: selected pool file is not a supported image: {Path}", chosenPath);
            return false;
        }

        try
        {
            await using var stream = File.OpenRead(chosenPath);
            await _providerManager.SaveImage(item, stream, mimeType, ImageType.Primary, null, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "PosterRotator: SaveImage failed for \"{Item}\" from {Path}", item.Name, chosenPath);
            return false;
        }

        try
        {
            await item.UpdateToRepositoryAsync(ItemUpdateType.ImageUpdate, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "PosterRotator: unable to notify Jellyfin repository for '{Name}'", item.Name);
        }

        return true;
    }

    private async Task<bool> IsAllowedRemoteImageUrlAsync(string url, Configuration cfg, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        if (!PluginHelpers.IsAllowedRemoteImageUri(uri, cfg.BlockPrivateNetworkImageUrls))
            return false;

        if (!cfg.BlockPrivateNetworkImageUrls || IPAddress.TryParse(uri.Host, out _))
            return true;

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(uri.Host, ct).ConfigureAwait(false);
            return addresses.Length > 0 && addresses.All(address => !PluginHelpers.IsPrivateOrLocalAddress(address));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "PosterRotator: DNS validation failed for {Host}", uri.Host);
            return false;
        }
    }

    private async Task<HttpResponseMessage> SendRemoteImageRequestAsync(
        HttpClient client,
        string initialUrl,
        Configuration cfg,
        CancellationToken ct)
    {
        var currentUrl = initialUrl;
        for (var redirectCount = 0; redirectCount <= MaxRemoteImageRedirects; redirectCount++)
        {
            if (!await IsAllowedRemoteImageUrlAsync(currentUrl, cfg, ct).ConfigureAwait(false))
                throw new InvalidOperationException("Remote image URL is not allowed.");

            var response = await client.GetAsync(currentUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!IsRedirectStatusCode(response.StatusCode))
                return response;

            var redirectUri = ResolveRemoteImageRedirectUri(currentUrl, response.Headers.Location);
            response.Dispose();
            if (redirectUri == null)
                throw new HttpRequestException("Remote image redirect is invalid.");

            currentUrl = redirectUri.AbsoluteUri;
        }

        throw new HttpRequestException("Remote image redirect limit exceeded.");
    }

    internal static bool IsRedirectStatusCode(HttpStatusCode statusCode)
        => statusCode is HttpStatusCode.Moved
            or HttpStatusCode.Redirect
            or HttpStatusCode.RedirectMethod
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;

    internal static Uri? ResolveRemoteImageRedirectUri(string currentUrl, Uri? location)
    {
        if (location == null || !Uri.TryCreate(currentUrl, UriKind.Absolute, out var currentUri))
            return null;

        return location.IsAbsoluteUri
            ? location
            : new Uri(currentUri, location);
    }

    internal static IReadOnlyList<RemoteImageDownloadCandidate> SelectRemoteImagesForLanguage(
        IEnumerable<RemoteImageInfo> images,
        Configuration cfg,
        string? originalLanguage,
        int remainingPreferredSlots)
    {
        var ordered = images.ToList();
        var result = new List<RemoteImageDownloadCandidate>();
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var preferredLanguage = NormalizeLanguageCode(cfg.PreferredLanguage) ?? "fr";

        void AddCandidates(IEnumerable<RemoteImageInfo> candidates, string? languageOverride, int? take = null)
        {
            var addedForGroup = 0;
            foreach (var image in candidates)
            {
                if (take.HasValue && addedForGroup >= take.Value)
                    return;

                if (string.IsNullOrWhiteSpace(image.Url) || !seenUrls.Add(image.Url))
                    continue;

                result.Add(new RemoteImageDownloadCandidate(image, languageOverride ?? NormalizeLanguageCode(image.Language) ?? "unknown"));
                addedForGroup++;
            }
        }

        var primaryImages = ordered.Where(image => image.Type == ImageType.Primary).ToList();
        if (remainingPreferredSlots > 0)
        {
            AddCandidates(
                primaryImages.Where(image => LanguageEquals(image.Language, preferredLanguage)),
                preferredLanguage,
                remainingPreferredSlots);
        }

        foreach (var fallbackLanguage in GetFallbackLanguageOrder(cfg, originalLanguage, preferredLanguage))
        {
            AddCandidates(
                primaryImages.Where(image => LanguageEquals(image.Language, fallbackLanguage)),
                fallbackLanguage);
        }

        if (cfg.IncludeUnknownLanguage)
        {
            AddCandidates(
                primaryImages.Where(image => NormalizeLanguageCode(image.Language) == null),
                "unknown");
        }

        if (cfg.AllowAnyLanguageFallback)
        {
            AddCandidates(
                ordered.Where(image => image.Type is ImageType.Primary or ImageType.Thumb or ImageType.Backdrop),
                languageOverride: null);
        }

        return result;
    }

    internal static IReadOnlyList<RemoteImageInfo> OrderRemoteImagesForDownload(IEnumerable<RemoteImageInfo> images) =>
        images
            .OrderByDescending(info => PreferredProviderScore(info.ProviderName ?? string.Empty))
            .ThenBy(info => info.ProviderName ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(info => ImageTypePriority(info.Type))
            .ToList();

    internal static string? NormalizeLanguageCode(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
            return null;

        var normalized = language.Trim().Replace('_', '-').ToLowerInvariant();
        var separator = normalized.IndexOf('-', StringComparison.Ordinal);
        if (separator > 0)
            normalized = normalized[..separator];

        return normalized.Length is >= 2 and <= 3 ? normalized : null;
    }

    private static bool LanguageEquals(string? value, string language) =>
        string.Equals(NormalizeLanguageCode(value), language, StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<string> GetFallbackLanguageOrder(
        Configuration cfg,
        string? originalLanguage,
        string preferredLanguage)
    {
        var original = NormalizeLanguageCode(originalLanguage);
        var configured = NormalizeLanguageCode(cfg.FallbackLanguage);
        var result = new List<string>(capacity: 2);

        void Add(string? language)
        {
            if (string.IsNullOrWhiteSpace(language)
                || language.Equals(preferredLanguage, StringComparison.OrdinalIgnoreCase)
                || result.Contains(language, StringComparer.OrdinalIgnoreCase))
            {
                return;
            }

            result.Add(language);
        }

        switch (cfg.FallbackMode)
        {
            case LanguageFallbackMode.ConfiguredThenOriginal:
                Add(configured);
                Add(original);
                break;
            case LanguageFallbackMode.OriginalOnly:
                Add(original);
                break;
            case LanguageFallbackMode.ConfiguredOnly:
                Add(configured);
                break;
            default:
                Add(original);
                Add(configured);
                break;
        }

        return result;
    }

    private static int ImageTypePriority(ImageType type) =>
        type switch
        {
            ImageType.Primary => 0,
            ImageType.Thumb => 1,
            ImageType.Backdrop => 2,
            _ => 3
        };

    private static long GetMaxDownloadBytes(Configuration cfg)
    {
        var megabytes = Math.Clamp(cfg.MaxDownloadMegabytes, 1, 200);
        return megabytes * 1024L * 1024L;
    }

    private string? GetOriginalLanguage(BaseItem item)
    {
        try
        {
            var originalLanguage = NormalizeLanguageCode(TryGetStringProperty(item, "OriginalLanguage"));
            if (!string.IsNullOrWhiteSpace(originalLanguage))
                return originalLanguage;

            var originalTitle = item.OriginalTitle;
            var name = item.Name;

            if (!string.IsNullOrEmpty(originalTitle)
                && !string.IsNullOrEmpty(name)
                && !originalTitle.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return DetectLanguageFromTitle(originalTitle);
            }

            var providerIds = item.ProviderIds;
            if (providerIds != null)
            {
                if (providerIds.ContainsKey("AniDB") || providerIds.ContainsKey("AniList"))
                    return "ja";

                if (item.Path?.Contains("Korean", StringComparison.OrdinalIgnoreCase) == true)
                    return "ko";
            }

            var path = item.Path;
            if (!string.IsNullOrEmpty(path))
            {
                var pathLower = path.ToLowerInvariant();
                if (pathLower.Contains("/japanese/") || pathLower.Contains("\\japanese\\") || pathLower.Contains("/anime/"))
                    return "ja";
                if (pathLower.Contains("/french/") || pathLower.Contains("\\french\\"))
                    return "fr";
                if (pathLower.Contains("/german/") || pathLower.Contains("\\german\\"))
                    return "de";
                if (pathLower.Contains("/spanish/") || pathLower.Contains("\\spanish\\"))
                    return "es";
                if (pathLower.Contains("/korean/") || pathLower.Contains("\\korean\\"))
                    return "ko";
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "PosterRotator: failed to detect original language for {Item}", item.Name);
        }

        return null;
    }

    private static string? TryGetStringProperty(object instance, string propertyName)
    {
        try
        {
            return instance.GetType().GetProperty(propertyName)?.GetValue(instance) as string;
        }
        catch
        {
            return null;
        }
    }

    private static string? DetectLanguageFromTitle(string title)
    {
        if (string.IsNullOrEmpty(title)) return null;

        foreach (var c in title)
        {
            if (c >= 0x3040 && c <= 0x309F) return "ja";
            if (c >= 0x30A0 && c <= 0x30FF) return "ja";
            if (c >= 0xAC00 && c <= 0xD7AF) return "ko";
            if (c >= 0x4E00 && c <= 0x9FFF)
            {
                var hasKana = title.Any(ch => ch >= 0x3040 && ch <= 0x30FF);
                if (!hasKana) return "zh";
            }

            if (c >= 0x0400 && c <= 0x04FF) return "ru";
            if (c >= 0x0600 && c <= 0x06FF) return "ar";
            if (c >= 0x0590 && c <= 0x05FF) return "he";
            if (c >= 0x0E00 && c <= 0x0E7F) return "th";
        }

        return null;
    }

    private static bool IsMixedFolder(BaseItem item)
    {
        var dir = Path.GetDirectoryName(item.Path ?? string.Empty) ?? string.Empty;
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            return false;

        try
        {
            return Directory
                .EnumerateFiles(dir)
                .Where(file => !PluginHelpers.IsSupportedImageExtension(file))
                .Take(2)
                .Count() > 1;
        }
        catch
        {
            return false;
        }
    }

    private static IEnumerable<string> GetPoolPatterns() =>
        new[] { "*.jpg", "*.jpeg", "*.png", "*.webp", "*.gif" };

    private static string? TryCopyCurrentPrimaryToPool(BaseItem item, string poolDir, bool mixedFolder = false)
    {
        try
        {
            foreach (var file in Directory.GetFiles(poolDir, "pool_currentprimary.*"))
                TryDeleteFile(file);

            var primary = item.GetImagePath(ImageType.Primary);
            if (!string.IsNullOrEmpty(primary) && File.Exists(primary) && PluginHelpers.TryGetImageFormat(primary, out var extension, out _))
            {
                var dest = Path.Combine(poolDir, "pool_original" + extension);
                File.Copy(primary, dest, overwrite: true);
                return dest;
            }

            if (mixedFolder && !string.IsNullOrEmpty(item.Path))
            {
                var dir = Path.GetDirectoryName(item.Path)!;
                var baseName = Path.GetFileNameWithoutExtension(item.Path)!;
                var existing = Directory.GetFiles(dir, $"{baseName}-poster.*")
                    .Concat(Directory.GetFiles(dir, $"{baseName}.*"))
                    .FirstOrDefault(PluginHelpers.IsSupportedImageExtension);

                if (!string.IsNullOrEmpty(existing) && PluginHelpers.TryGetImageFormat(existing, out var existingExtension, out _))
                {
                    var dest = Path.Combine(poolDir, "pool_original" + existingExtension);
                    File.Copy(existing, dest, overwrite: true);
                    return dest;
                }
            }
        }
        catch
        {
            // Best-effort fallback only.
        }

        return null;
    }

    private static string PickNextFor(List<string> files, BaseItem item, Configuration cfg, RotationState state)
    {
        var reordered = files
            .OrderBy(file => Path.GetFileName(file).StartsWith("pool_original", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ThenBy(file => Path.GetFileName(file), StringComparer.OrdinalIgnoreCase)
            .ToList();

        var key = item.Id.ToString();
        int idx;

        if (cfg.SequentialRotation)
        {
            if (!state.LastIndexByItem.ContainsKey(key) && reordered.Count > 1)
            {
                idx = 1;
                state.LastIndexByItem[key] = 2;
            }
            else
            {
                var last = state.LastIndexByItem.TryGetValue(key, out var value) ? value : 0;
                idx = last % reordered.Count;
                state.LastIndexByItem[key] = last + 1;
            }
        }
        else if (reordered.Count > 1)
        {
            var nonOriginal = reordered
                .Where(file => !Path.GetFileName(file).StartsWith("pool_original", StringComparison.OrdinalIgnoreCase))
                .ToList();
            idx = nonOriginal.Count > 0
                ? reordered.IndexOf(nonOriginal[Random.Shared.Next(nonOriginal.Count)])
                : Random.Shared.Next(reordered.Count);
        }
        else
        {
            idx = 0;
        }

        return reordered[idx];
    }

    private static int PreferredProviderScore(string providerName)
    {
        if (string.IsNullOrEmpty(providerName)) return 0;
        var name = providerName.ToLowerInvariant();
        if (name.Contains("tvdb") || name.Contains("thetvdb")) return 100;
        if (name.Contains("tmdb")) return 50;
        if (name.Contains("fanart")) return 40;
        return 0;
    }

    private static bool IsLandscape(RemoteImageInfo info) =>
        info.Width > 0 && info.Height > 0 && info.Width > info.Height;

    private Dictionary<string, List<string>> GetLibraryRootPaths()
    {
        try
        {
            var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var virtualFolder in _library.GetVirtualFolders())
            {
                var name = virtualFolder.Name ?? string.Empty;
                if (!map.ContainsKey(name))
                    map[name] = new List<string>();

                if (virtualFolder.Locations == null)
                    continue;

                foreach (var location in virtualFolder.Locations.Where(location => !string.IsNullOrWhiteSpace(location)))
                    map[name].Add(location);
            }

            foreach (var key in map.Keys.ToList())
                map[key] = map[key].Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            return map;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "PosterRotator: unable to read virtual folders.");
            return new Dictionary<string, List<string>>();
        }
    }

    private List<Guid> GetSelectedLibraryTopParentIds(IReadOnlyCollection<string> selectedLibraryNames)
    {
        if (selectedLibraryNames.Count == 0)
            return new List<Guid>();

        try
        {
            var selected = selectedLibraryNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
            return _library.GetVirtualFolders()
                .Where(folder => !string.IsNullOrWhiteSpace(folder.Name) && selected.Contains(folder.Name))
                .Select(folder => Guid.TryParse(folder.ItemId, out var id) ? id : Guid.Empty)
                .Where(id => id != Guid.Empty)
                .Distinct()
                .ToList();
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "PosterRotator: unable to resolve selected library parent ids.");
            return new List<Guid>();
        }
    }

    private void QueueLibraryScanIfRequested(Configuration cfg, int rotatedCount)
    {
        if (!cfg.TriggerLibraryScanAfterRotation || rotatedCount <= 0)
            return;

        try
        {
            _library.QueueLibraryScan();
            _log.LogInformation("PosterRotator: queued a library scan after rotating {Count} item(s).", rotatedCount);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "PosterRotator: unable to queue library scan after poster rotation.");
        }
    }

    public async Task<PoolDiagnostics> GetDiagnosticsAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        if (_cachedDiagnostics != null && now - _cachedDiagnosticsUtc < DiagnosticsCacheDuration)
            return _cachedDiagnostics;

        await _diagnosticsLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            now = DateTimeOffset.UtcNow;
            if (_cachedDiagnostics != null && now - _cachedDiagnosticsUtc < DiagnosticsCacheDuration)
                return _cachedDiagnostics;

            var indexedIds = (await _poolStore.GetIndexEntriesAsync(cancellationToken).ConfigureAwait(false))
                .Select(entry => Guid.TryParse(entry.ItemId, out var itemId) ? itemId : Guid.Empty)
                .Where(itemId => itemId != Guid.Empty)
                .Distinct()
                .ToArray();
            var existingIds = GetExistingIndexedItemIds(indexedIds, cancellationToken);
            var diagnostics = await _poolStore.GetDiagnosticsAsync(existingIds, cancellationToken).ConfigureAwait(false);
            _cachedDiagnostics = diagnostics;
            _cachedDiagnosticsUtc = now;
            return diagnostics;
        }
        finally
        {
            _diagnosticsLock.Release();
        }
    }

    private HashSet<Guid> GetExistingIndexedItemIds(Guid[] indexedIds, CancellationToken cancellationToken)
    {
        var existingIds = new HashSet<Guid>();
        foreach (var batch in indexedIds.Chunk(5000))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var id in _library.GetItemIds(new InternalItemsQuery
            {
                ItemIds = batch.ToArray(),
                EnableTotalRecordCount = false
            }))
            {
                existingIds.Add(id);
            }
        }

        return existingIds;
    }

    public Task<PoolListResponse> ListPoolsAsync(PoolListQuery query, CancellationToken cancellationToken) =>
        _poolStore.ListPoolsAsync(query, cancellationToken);

    public Task<PoolRebuildIndexResult> RebuildPoolIndexAsync(CancellationToken cancellationToken) =>
        _poolStore.RebuildIndexAsync(cancellationToken);

    private async Task AnnotateCurrentPosterAsync(Guid itemId, PoolMetadata metadata, CancellationToken cancellationToken)
    {
        MarkCurrentPoster(metadata, null, "none", primaryImageFound: false);

        var item = TryGetItemById(itemId);
        var primaryPath = TryGetPrimaryImagePath(item);
        var primaryImageFound = !string.IsNullOrWhiteSpace(primaryPath) && File.Exists(primaryPath);

        if (primaryImageFound)
        {
            var currentHash = await ImageHash.ComputeNormalizedHashAsync(primaryPath!, _imageProcessor, cancellationToken).ConfigureAwait(false);
            if (currentHash != 0)
            {
                var storedHashMatch = metadata.Images.FirstOrDefault(image =>
                    image.Hash != 0
                    && ImageHash.HammingDistance(currentHash, image.Hash) <= ImageHash.CurrentPosterMatchThreshold);
                if (storedHashMatch != null)
                {
                    MarkCurrentPoster(metadata, storedHashMatch.FileName, "stored-hash", primaryImageFound);
                    return;
                }

                var visualMatch = await FindPoolImageByCurrentHashAsync(itemId, metadata, currentHash, cancellationToken).ConfigureAwait(false);
                if (visualMatch != null)
                {
                    MarkCurrentPoster(metadata, visualMatch.FileName, "visual-hash", primaryImageFound);
                    return;
                }
            }
        }

        if (!MarkLastAppliedAsCurrent(metadata, primaryImageFound))
            MarkCurrentPoster(metadata, null, "none", primaryImageFound);
    }

    private async Task<PoolImageMetadata?> FindPoolImageByCurrentHashAsync(
        Guid itemId,
        PoolMetadata metadata,
        ulong currentHash,
        CancellationToken cancellationToken)
    {
        foreach (var image in metadata.Images)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var file = await _poolStore.GetImageFileAsync(itemId, image.FileName, cancellationToken).ConfigureAwait(false);
                var imageHash = await ImageHash.ComputeNormalizedHashAsync(file.Path, _imageProcessor, cancellationToken).ConfigureAwait(false);
                if (imageHash != 0
                    && ImageHash.HammingDistance(currentHash, imageHash) <= ImageHash.CurrentPosterMatchThreshold)
                {
                    return image;
                }
            }
            catch (FileNotFoundException)
            {
            }
            catch (InvalidDataException)
            {
            }
        }

        return null;
    }

    private static string? TryGetPrimaryImagePath(BaseItem? item)
    {
        if (item == null)
            return null;

        try
        {
            return item.GetImagePath(ImageType.Primary);
        }
        catch
        {
            return null;
        }
    }

    internal static bool MarkLastAppliedAsCurrent(PoolMetadata metadata, bool primaryImageFound)
    {
        var lastApplied = metadata.Images
            .Where(image => image.LastAppliedUtc.HasValue)
            .OrderByDescending(image => image.LastAppliedUtc)
            .ThenBy(image => image.FileName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (lastApplied == null)
            return false;

        MarkCurrentPoster(metadata, lastApplied.FileName, "last-applied", primaryImageFound);
        return true;
    }

    internal static void MarkCurrentPoster(
        PoolMetadata metadata,
        string? fileName,
        string matchMethod,
        bool primaryImageFound)
    {
        foreach (var image in metadata.Images)
            image.IsCurrent = !string.IsNullOrWhiteSpace(fileName)
                && image.FileName.Equals(fileName, StringComparison.OrdinalIgnoreCase);

        metadata.CurrentPoster = new PoolCurrentPosterInfo
        {
            PrimaryImageFound = primaryImageFound,
            Matched = !string.IsNullOrWhiteSpace(fileName),
            FileName = fileName,
            MatchMethod = string.IsNullOrWhiteSpace(matchMethod) ? "none" : matchMethod
        };
    }

    public async Task<PoolMetadata?> GetPoolAsync(Guid itemId, CancellationToken cancellationToken)
    {
        var pool = await _poolStore.GetPoolAsync(itemId, reconcileFiles: true, cancellationToken).ConfigureAwait(false);
        if (pool != null)
            await AnnotateCurrentPosterAsync(itemId, pool, cancellationToken).ConfigureAwait(false);

        return pool;
    }

    public Task<PoolImageFile> GetPoolImageAsync(Guid itemId, string fileName, CancellationToken cancellationToken) =>
        _poolStore.GetImageFileAsync(itemId, fileName, cancellationToken);

    public async Task<PoolOperationResult> RotatePoolNowAsync(Guid itemId, CancellationToken cancellationToken)
    {
        using var poolLock = await _poolStore.LockPoolAsync(itemId, cancellationToken).ConfigureAwait(false);
        var item = TryGetItemById(itemId);
        if (item == null)
        {
            return new PoolOperationResult
            {
                Success = false,
                ItemId = itemId.ToString(),
                Message = "Media introuvable dans Jellyfin."
            };
        }

        var poolDir = _poolStore.TryGetPoolDirectory(itemId, create: false);
        if (poolDir == null || !Directory.Exists(poolDir))
        {
            return new PoolOperationResult
            {
                Success = false,
                ItemId = itemId.ToString(),
                Message = "Pool introuvable dans le dossier plugin."
            };
        }

        var snapshot = CreateSnapshot(item, GetLibraryRootPaths());
        var metadata = await _poolStore.EnsurePoolAsync(snapshot, poolDir, cancellationToken).ConfigureAwait(false);
        var files = LoadLocalPoolFiles(poolDir).ToList();
        if (files.Count == 0)
        {
            await _poolStore.RecordErrorAsync(itemId, "Rotation immediate impossible: pool vide.", cancellationToken).ConfigureAwait(false);
            return new PoolOperationResult
            {
                Success = false,
                ItemId = itemId.ToString(),
                Message = "Pool vide."
            };
        }

        var cfg = Plugin.Instance?.Configuration ?? new Configuration();
        var state = CreateRotationState(item, metadata, Path.Combine(poolDir, "rotation_state.json"));
        var chosen = PickNextFor(files, item, cfg, state);
        if (!await SavePrimaryImageAsync(item, chosen, cancellationToken).ConfigureAwait(false))
        {
            await _poolStore.RecordErrorAsync(itemId, "Rotation immediate impossible: SaveImage a echoue.", cancellationToken).ConfigureAwait(false);
            return new PoolOperationResult
            {
                Success = false,
                ItemId = itemId.ToString(),
                FileName = Path.GetFileName(chosen),
                Message = "Jellyfin n'a pas accepte l'image selectionnee."
            };
        }

        await _poolStore.RecordRotationAsync(
            snapshot,
            poolDir,
            chosen,
            state.LastIndexByItem.GetValueOrDefault(item.Id.ToString()),
            DateTimeOffset.UtcNow,
            cancellationToken).ConfigureAwait(false);

        return new PoolOperationResult
        {
            Success = true,
            ItemId = itemId.ToString(),
            FileName = Path.GetFileName(chosen),
            ProcessedCount = 1,
            RotatedCount = 1,
            Message = "Rotation effectuee."
        };
    }

    public async Task<PoolOperationResult> RotateLibraryNowAsync(string libraryName, CancellationToken cancellationToken)
    {
        var processed = 0;
        var rotated = 0;
        var failed = 0;
        var start = 0;
        const int limit = 200;

        while (true)
        {
            var list = await _poolStore.ListPoolsAsync(
                new PoolListQuery { Library = libraryName, Start = start, Limit = limit },
                cancellationToken).ConfigureAwait(false);

            foreach (var entry in list.Items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!Guid.TryParse(entry.ItemId, out var itemId))
                {
                    failed++;
                    continue;
                }

                processed++;
                var result = await RotatePoolNowAsync(itemId, cancellationToken).ConfigureAwait(false);
                if (result.Success)
                    rotated++;
                else
                    failed++;
            }

            start += list.Items.Count;
            if (start >= list.Total || list.Items.Count == 0)
                break;
        }

        return new PoolOperationResult
        {
            Success = failed == 0,
            ProcessedCount = processed,
            RotatedCount = rotated,
            FailedCount = failed,
            Message = $"{rotated}/{processed} pool(s) tournes."
        };
    }

    public async Task<PoolImageMetadata> ImportPoolImageAsync(
        Guid itemId,
        Stream stream,
        string fileName,
        CancellationToken cancellationToken)
    {
        using var poolLock = await _poolStore.LockPoolAsync(itemId, cancellationToken).ConfigureAwait(false);
        var item = TryGetItemById(itemId) ?? throw new FileNotFoundException("Media introuvable dans Jellyfin.");
        var snapshot = CreateSnapshot(item, GetLibraryRootPaths());
        var cfg = Plugin.Instance?.Configuration ?? new Configuration();
        return await _poolStore.ImportImageAsync(snapshot, stream, fileName, cfg, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PoolImageMetadata> DeletePoolImageAsync(Guid itemId, string fileName, CancellationToken cancellationToken)
    {
        using var poolLock = await _poolStore.LockPoolAsync(itemId, cancellationToken).ConfigureAwait(false);
        return await _poolStore.DeleteImageAsync(itemId, fileName, cancellationToken).ConfigureAwait(false);
    }

    public Task<PurgePoolsResult> PurgeAsync(PoolPurgeRequest request, CancellationToken cancellationToken) =>
        _poolStore.PurgeAsync(request, ItemExists, cancellationToken);

    private bool ItemExists(Guid itemId) => TryGetItemById(itemId) != null;

    private BaseItem? TryGetItemById(Guid itemId)
    {
        try
        {
            return _library.GetItemById(itemId);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "PosterRotator: unable to resolve item {ItemId}", itemId);
            return null;
        }
    }

    public async Task<PurgePoolsResult> PurgeAllPoolsAsync(CancellationToken cancellationToken)
    {
        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var result = new PurgePoolsResult();
            PurgePluginDataPools(result);
            PurgeLegacyMediaPools(result, cancellationToken);

            _log.LogInformation(
                "PosterRotator: purge complete - {Deleted} deleted, {Skipped} skipped, {Failed} failed",
                result.DeletedCount,
                result.SkippedCount,
                result.FailedCount);

            return result;
        }
        finally
        {
            _operationLock.Release();
        }
    }

    private void PurgePluginDataPools(PurgePoolsResult result)
    {
        var poolRoot = _poolStore.TryGetPoolRootPath(create: false);
        if (string.IsNullOrWhiteSpace(poolRoot))
            return;

        if (!Directory.Exists(poolRoot))
            return;

        foreach (var poolDir in Directory.EnumerateDirectories(poolRoot))
            TryDeleteSafeDirectory(poolDir, poolRoot, requireLegacyPoolName: false, result);

        TryDeleteFile(Path.Combine(poolRoot, "index.json"));
        _poolStore.InvalidateIndexCache();
    }

    private void PurgeLegacyMediaPools(PurgePoolsResult result, CancellationToken cancellationToken)
    {
        var roots = GetLibraryRootPaths()
            .Values
            .SelectMany(paths => paths)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var root in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(root) || IsReparsePoint(root))
            {
                result.SkippedCount++;
                continue;
            }

            foreach (var poolDir in EnumerateLegacyPoolDirectories(root, cancellationToken))
                TryDeleteSafeDirectory(poolDir, root, requireLegacyPoolName: true, result);
        }
    }

    private IEnumerable<string> EnumerateLegacyPoolDirectories(string root, CancellationToken cancellationToken)
    {
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = pending.Pop();
            IEnumerable<string> children;

            try
            {
                children = Directory.EnumerateDirectories(current);
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "PosterRotator: cannot scan {Directory}", current);
                continue;
            }

            foreach (var child in children)
            {
                if (IsReparsePoint(child))
                    continue;

                if (LegacyPoolDirectoryName.Equals(Path.GetFileName(child), StringComparison.OrdinalIgnoreCase))
                {
                    yield return child;
                    continue;
                }

                pending.Push(child);
            }
        }
    }

    private void TryDeleteSafeDirectory(string directory, string root, bool requireLegacyPoolName, PurgePoolsResult result)
    {
        try
        {
            if (!Directory.Exists(directory) || !PluginHelpers.IsPathInsideOrEqual(directory, root) || IsReparsePoint(directory))
            {
                result.SkippedCount++;
                return;
            }

            if (requireLegacyPoolName && !PluginHelpers.IsSafePoolDirectory(directory, root))
            {
                result.SkippedCount++;
                return;
            }

            if (DirectoryContainsReparsePoint(directory))
            {
                result.SkippedCount++;
                _log.LogWarning("PosterRotator: skipped purge for {Directory} because it contains a reparse point.", directory);
                return;
            }

            Directory.Delete(directory, recursive: true);
            result.DeletedCount++;
        }
        catch (Exception ex)
        {
            result.FailedCount++;
            _log.LogWarning(ex, "PosterRotator: failed to delete pool {Directory}", directory);
        }
    }

    private static bool DirectoryContainsReparsePoint(string directory)
    {
        var pending = new Stack<string>();
        pending.Push(directory);

        while (pending.Count > 0)
        {
            var current = pending.Pop();
            string[] entries;

            try
            {
                entries = Directory.GetFileSystemEntries(current);
            }
            catch
            {
                return true;
            }

            foreach (var entry in entries)
            {
                if (IsReparsePoint(entry))
                    return true;

                if (Directory.Exists(entry))
                    pending.Push(entry);
            }
        }

        return false;
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            return true;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: false);
        }
        catch
        {
        }
    }

    private static bool IsDirectoryEmpty(string path)
    {
        try
        {
            return Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any();
        }
        catch
        {
            return false;
        }
    }

    private static void TryWriteText(string path, string text)
    {
        try
        {
            File.WriteAllText(path, text);
        }
        catch
        {
        }
    }
}
