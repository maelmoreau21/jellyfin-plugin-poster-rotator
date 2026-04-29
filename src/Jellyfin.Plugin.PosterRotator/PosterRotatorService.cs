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
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PosterRotator;

public class PosterRotatorService
{
    private const string LegacyPoolDirectoryName = ".poster_pool";
    private const string PluginPoolDirectoryName = "pools";
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private readonly ILibraryManager _library;
    private readonly IProviderManager _providerManager;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<PosterRotatorService> _log;

    public PosterRotatorService(
        ILibraryManager library,
        IProviderManager providerManager,
        IHttpClientFactory httpFactory,
        ILogger<PosterRotatorService> log)
    {
        _library = library;
        _providerManager = providerManager;
        _httpFactory = httpFactory;
        _log = log;
    }

    public async Task RunAsync(Configuration cfg, IProgress<double>? progress, CancellationToken ct)
    {
        await _operationLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await RunInternalAsync(cfg, progress, ct).ConfigureAwait(false);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    private async Task RunInternalAsync(Configuration cfg, IProgress<double>? progress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        int rotatedCount = 0, skippedCount = 0, errorCount = 0, topUpCount = 0;

        var kinds = new List<BaseItemKind>
        {
            BaseItemKind.Movie,
            BaseItemKind.Series,
            BaseItemKind.BoxSet
        };

        if (cfg.EnableSeasonPosters) kinds.Add(BaseItemKind.Season);
        if (cfg.EnableEpisodePosters) kinds.Add(BaseItemKind.Episode);

        var query = new InternalItemsQuery
        {
            IncludeItemTypes = kinds.Distinct().ToArray(),
            Recursive = true
        };

        var items = _library.GetItemList(query).ToList();
        if (items.Count == 0)
        {
            _log.LogWarning("PosterRotator: no items returned by library manager; aborting run.");
            return;
        }

        var dirCounts = items
            .Select(item => Path.GetDirectoryName(item.Path ?? string.Empty) ?? string.Empty)
            .GroupBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

        var libraryMap = GetLibraryRootPaths();
        var allLibraryRoots = libraryMap
            .SelectMany(kv => kv.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var rootsToNudge = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var configuredNames = new List<string>();
        if (cfg.LibraryRules is { Count: > 0 })
            configuredNames.AddRange(cfg.LibraryRules.Where(rule => rule.Enabled).Select(rule => rule.Name));
        else if (cfg.Libraries is { Count: > 0 })
            configuredNames.AddRange(cfg.Libraries);

        var selection = ResolveSelectedRoots(cfg, configuredNames, libraryMap, allLibraryRoots);
        var hasSelection = selection.Paths.Count > 0 || selection.LibraryNames.Count > 0;
        var total = items.Count;
        var done = 0;

        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();

            if (hasSelection && !MatchesSelection(item.Path ?? string.Empty, selection, libraryMap))
            {
                skippedCount++;
                progress?.Report(++done * 100.0 / Math.Max(1, total));
                continue;
            }

            try
            {
                var result = await ProcessItemAsync(item, cfg, ct, dirCounts).ConfigureAwait(false);
                if (result.Rotated)
                {
                    rotatedCount++;
                    if (cfg.PoolStorageMode == PoolStorageMode.MediaFolders)
                    {
                        var root = allLibraryRoots.FirstOrDefault(r => PluginHelpers.IsPathInsideOrEqual(item.Path ?? string.Empty, r));
                        if (!string.IsNullOrEmpty(root))
                            rootsToNudge.Add(root);
                    }
                }

                topUpCount += result.TopUps;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                errorCount++;
                _log.LogWarning(ex, "PosterRotator: error processing \"{Name}\" ({Path})", item.Name, item.Path);
            }

            progress?.Report(++done * 100.0 / Math.Max(1, total));
        }

        foreach (var root in rootsToNudge)
            NudgeLibraryRootLegacy(root);

        sw.Stop();
        _log.LogInformation(
            "PosterRotator: run complete - {Rotated}/{Total} rotated, {TopUp} top-ups, {Skipped} skipped, {Errors} errors, took {Elapsed:0.0}s",
            rotatedCount,
            total,
            topUpCount,
            skippedCount,
            errorCount,
            sw.Elapsed.TotalSeconds);
    }

    private SelectedRoots ResolveSelectedRoots(
        Configuration cfg,
        List<string> configuredNames,
        Dictionary<string, List<string>> libraryMap,
        List<string> allLibraryRoots)
    {
        var result = new SelectedRoots();

        if (cfg.ManualLibraryRoots is { Count: > 0 })
        {
            var manual = cfg.ManualLibraryRoots
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var entry in manual)
            {
                if (LooksLikePath(entry))
                {
                    result.Paths.Add(entry);
                    continue;
                }

                if (libraryMap.TryGetValue(entry, out var paths) && paths is { Count: > 0 })
                {
                    result.Paths.AddRange(paths);
                    result.LibraryNames.Add(entry);
                }
                else
                {
                    _log.LogWarning("PosterRotator: manual entry '{Entry}' does not match any known library", entry);
                }
            }

            DeduplicatePaths(result.Paths);
            if (result.Paths.Count > 0)
            {
                _log.LogInformation("PosterRotator: using ManualLibraryRoots resolved to: {Roots}", string.Join(",", result.Paths));
                return result;
            }
        }

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

    private static bool LooksLikePath(string entry) =>
        entry.IndexOf(':') >= 0 || entry.IndexOf('\\') >= 0 || entry.IndexOf('/') >= 0;

    private async Task<(bool Rotated, int TopUps)> ProcessItemAsync(
        BaseItem item,
        Configuration cfg,
        CancellationToken ct,
        IDictionary<string, int> dirCounts)
    {
        var itemDir = PluginHelpers.GetItemDirectory(item.Path) ?? string.Empty;
        var storageMode = ResolveStorageMode(cfg);

        if (storageMode == PoolStorageMode.MediaFolders && (string.IsNullOrEmpty(itemDir) || !Directory.Exists(itemDir)))
            return (false, 0);

        var legacyPoolDir = string.IsNullOrEmpty(itemDir) ? null : Path.Combine(itemDir, LegacyPoolDirectoryName);
        var poolDir = ResolvePoolDirectory(item, storageMode, legacyPoolDir);
        if (string.IsNullOrEmpty(poolDir))
            return (false, 0);

        Directory.CreateDirectory(poolDir);

        if (storageMode == PoolStorageMode.PluginData && !string.IsNullOrEmpty(legacyPoolDir))
            MigrateLegacyPoolIfNeeded(legacyPoolDir, poolDir);

        var local = LoadLocalPoolFiles(poolDir);
        var lockFile = Path.Combine(poolDir, "pool.lock");
        var poolIsLocked = File.Exists(lockFile);
        var statePath = Path.Combine(poolDir, "rotation_state.json");
        var state = PluginHelpers.LoadRotationState(statePath);
        var key = item.Id.ToString();
        var now = DateTimeOffset.UtcNow;
        var minHours = Math.Max(1, cfg.MinHoursBetweenSwitches);
        var poolSize = Math.Clamp(cfg.PoolSize, 1, 50);
        var haveLast = state.LastRotatedUtcByItem.TryGetValue(key, out var lastEpoch);
        var elapsed = haveLast ? now - DateTimeOffset.FromUnixTimeSeconds(lastEpoch) : TimeSpan.MaxValue;
        var allowTopUp = !haveLast || elapsed.TotalHours >= minHours || local.Count == 0;

        if (_log.IsEnabled(LogLevel.Debug))
        {
            _log.LogDebug(
                "PosterRotator: \"{Item}\" pool has {Count}/{Target}. Storage:{Storage}. Locked:{Locked}. AllowTopUp:{Allow}",
                item.Name,
                local.Count,
                poolSize,
                storageMode,
                poolIsLocked,
                allowTopUp);
        }

        var topUpCount = 0;
        if (!poolIsLocked && local.Count < poolSize && allowTopUp)
        {
            var added = await TryTopUpFromProvidersAsync(item, poolDir, poolSize - local.Count, cfg, ct).ConfigureAwait(false);
            topUpCount = added.Count;
            foreach (var file in added)
                local.Add(file);

            if (cfg.LockImagesAfterFill && local.Count >= poolSize)
            {
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

        if (local.Count == 0)
        {
            var primaryPath = TryCopyCurrentPrimaryToPool(item, poolDir, IsMixedFolder(item, dirCounts));
            if (primaryPath != null)
                local.Add(primaryPath);
        }

        if (local.Count == 0)
            return (false, topUpCount);

        var chosen = PickNextFor(local.ToList(), item, cfg, state);
        if (!await SavePrimaryImageAsync(item, chosen, ct).ConfigureAwait(false))
            return (false, topUpCount);

        state.LastRotatedUtcByItem[key] = now.ToUnixTimeSeconds();
        PluginHelpers.SaveRotationState(statePath, state);
        _log.LogInformation("PosterRotator: rotated \"{Item}\" -> {Poster}", item.Name, Path.GetFileName(chosen));

        return (true, topUpCount);
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

        var dataFolder = Plugin.Instance?.DataFolderPath;
        if (string.IsNullOrWhiteSpace(dataFolder))
            return legacyPoolDir;

        return Path.Combine(dataFolder, PluginPoolDirectoryName, item.Id.ToString("N"));
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

    private void MigrateLegacyPoolIfNeeded(string legacyPoolDir, string pluginPoolDir)
    {
        if (!Directory.Exists(legacyPoolDir))
            return;

        if (PluginHelpers.IsPathInsideOrEqual(legacyPoolDir, pluginPoolDir)
            || PluginHelpers.IsPathInsideOrEqual(pluginPoolDir, legacyPoolDir))
            return;

        try
        {
            Directory.CreateDirectory(pluginPoolDir);
            MoveDirectoryContentsSafely(legacyPoolDir, pluginPoolDir);
            Directory.Delete(legacyPoolDir, recursive: true);
            _log.LogInformation("PosterRotator: migrated legacy pool {LegacyPool} to plugin data.", legacyPoolDir);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "PosterRotator: failed to migrate legacy pool {LegacyPool}", legacyPoolDir);
        }
    }

    private void MoveDirectoryContentsSafely(string sourceDir, string destinationDir)
    {
        foreach (var sourceFile in Directory.GetFiles(sourceDir))
        {
            var info = new FileInfo(sourceFile);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                _log.LogWarning("PosterRotator: skipped reparse-point file during pool migration: {File}", sourceFile);
                continue;
            }

            var destination = Path.Combine(destinationDir, info.Name);
            if (File.Exists(destination))
            {
                TryDeleteFile(sourceFile);
                continue;
            }

            File.Move(sourceFile, destination);
        }

        foreach (var sourceChild in Directory.GetDirectories(sourceDir))
        {
            var info = new DirectoryInfo(sourceChild);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                _log.LogWarning("PosterRotator: skipped reparse-point directory during pool migration: {Directory}", sourceChild);
                continue;
            }

            var destinationChild = Path.Combine(destinationDir, info.Name);
            Directory.CreateDirectory(destinationChild);
            MoveDirectoryContentsSafely(sourceChild, destinationChild);
            Directory.Delete(sourceChild, recursive: false);
        }
    }

    private async Task<List<string>> TryTopUpFromProvidersAsync(
        BaseItem item,
        string poolDir,
        int needed,
        Configuration cfg,
        CancellationToken ct)
    {
        var added = new List<string>();
        var urlMapPath = Path.Combine(poolDir, "pool_urls.json");
        var urlMap = PluginHelpers.LoadJsonMap(urlMapPath);
        var knownUrls = new HashSet<string>(urlMap.Values, StringComparer.OrdinalIgnoreCase);

        try
        {
            var providers = _providerManager.GetImageProviders(item, null);
            IReadOnlyList<IRemoteImageProvider> providerList = providers
                .OfType<IRemoteImageProvider>()
                .Where(provider => ProviderSupportsPrimary(provider, item))
                .OrderByDescending(provider => PreferredProviderScore(provider.GetType().Name))
                .ToList();

            foreach (var provider in providerList)
            {
                if (added.Count >= needed)
                    break;

                try
                {
                    var images = await PluginHelpers.RetryAsync(
                        () => provider.GetImages(item, ct),
                        maxRetries: 3,
                        _log,
                        ct).ConfigureAwait(false);

                    await Harvest(images, preferPrimary: true).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _log.LogDebug(ex, "PosterRotator: provider {Provider} failed for \"{Item}\"", provider.GetType().Name, item.Name);
                }
            }

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

        async Task Harvest(IEnumerable<RemoteImageInfo>? images, bool preferPrimary)
        {
            if (images == null)
                return;

            var imageList = images.ToList();
            if (imageList.Count == 0)
                return;

            if (cfg.EnableLanguageFilter)
            {
                var preferredLanguage = (cfg.PreferredLanguage ?? "fr").ToLowerInvariant();
                var fallbackLanguage = cfg.UseOriginalLanguageAsFallback
                    ? (GetOriginalLanguage(item) ?? string.Empty).ToLowerInvariant()
                    : (cfg.FallbackLanguage ?? string.Empty).ToLowerInvariant();
                var remainingPreferredSlots = Math.Max(
                    0,
                    cfg.MaxPreferredLanguageImages - CountLanguageImagesInPool(poolDir, preferredLanguage));

                var preferredImages = imageList
                    .Where(info => info.Type == ImageType.Primary)
                    .Where(info => !string.IsNullOrEmpty(info.Language)
                        && info.Language.Equals(preferredLanguage, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                var fallbackImages = imageList
                    .Where(info => info.Type == ImageType.Primary)
                    .Where(info => string.IsNullOrEmpty(fallbackLanguage)
                        || (!string.IsNullOrEmpty(info.Language) && info.Language.Equals(fallbackLanguage, StringComparison.OrdinalIgnoreCase))
                        || (cfg.IncludeUnknownLanguage && string.IsNullOrEmpty(info.Language)))
                    .Where(info => !preferredImages.Contains(info))
                    .ToList();

                foreach (var info in preferredImages.Take(remainingPreferredSlots))
                {
                    if (added.Count >= needed) return;
                    await TryDownloadRemote(info, item, poolDir, preferredLanguage, knownUrls, urlMapPath).ConfigureAwait(false);
                }

                foreach (var info in fallbackImages)
                {
                    if (added.Count >= needed) return;
                    await TryDownloadRemote(info, item, poolDir, info.Language ?? "unknown", knownUrls, urlMapPath).ConfigureAwait(false);
                }
            }
            else
            {
                var ordered = preferPrimary
                    ? imageList.OrderByDescending(info => info.Type == ImageType.Primary).ThenBy(info => info.ProviderName).ToList()
                    : imageList.OrderBy(info => info.ProviderName).ToList();

                foreach (var info in ordered.Where(info => info.Type == ImageType.Primary))
                {
                    if (added.Count >= needed) return;
                    await TryDownloadRemote(info, item, poolDir, null, knownUrls, urlMapPath).ConfigureAwait(false);
                }

                foreach (var info in ordered.Where(info => info.Type is ImageType.Thumb or ImageType.Backdrop))
                {
                    if (added.Count >= needed) return;
                    await TryDownloadRemote(info, item, poolDir, null, knownUrls, urlMapPath).ConfigureAwait(false);
                }
            }
        }

        int CountLanguageImagesInPool(string dir, string language)
        {
            if (string.IsNullOrWhiteSpace(language) || !Directory.Exists(dir))
                return 0;

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

            var maxBytes = GetMaxDownloadBytes(cfg);
            var baseName = $"pool_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}_{Guid.NewGuid():N}";
            var tmpPath = Path.Combine(dir, baseName + ".tmp");

            try
            {
                using var client = _httpFactory.CreateClient("PosterRotator");
                using var response = await PluginHelpers.RetryAsync(
                    async () =>
                    {
                        var resp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
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

                var finalPath = Path.Combine(dir, baseName + extension);
                File.Move(tmpPath, finalPath);

                if (cfg.EnableDuplicateDetection)
                {
                    var hash = ImageHash.ComputeHash(finalPath);
                    if (hash != 0)
                    {
                        var existingHashes = ImageHash.LoadHashes(dir);
                        if (ImageHash.IsDuplicate(hash, existingHashes.Values))
                        {
                            _log.LogInformation("PosterRotator: rejected {Name} - visually duplicate", Path.GetFileName(finalPath));
                            TryDeleteFile(finalPath);
                            return;
                        }

                        ImageHash.SaveHash(dir, Path.GetFileName(finalPath), hash);
                    }
                }

                added.Add(finalPath);
                PluginHelpers.UpdateJsonMapFile(Path.Combine(dir, "pool_languages.json"), Path.GetFileName(finalPath), language ?? info.Language ?? "unknown");
                PluginHelpers.UpdateJsonMapFile(urlMapPath, Path.GetFileName(finalPath), url);
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
                throw;
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "PosterRotator: download failed for {Url} ({Item})", url, mediaItem.Name);
                TryDeleteFile(tmpPath);
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

    private static bool ProviderSupportsPrimary(IRemoteImageProvider provider, BaseItem item)
    {
        try
        {
            return provider.Supports(item) && provider.GetSupportedImages(item).Contains(ImageType.Primary);
        }
        catch
        {
            return false;
        }
    }

    private static long GetMaxDownloadBytes(Configuration cfg)
    {
        var megabytes = Math.Clamp(cfg.MaxDownloadMegabytes, 1, 200);
        return megabytes * 1024L * 1024L;
    }

    private string? GetOriginalLanguage(BaseItem item)
    {
        try
        {
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

        return "en";
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

        return "en";
    }

    private static bool IsMixedFolder(BaseItem item, IDictionary<string, int> dirCounts)
    {
        var dir = Path.GetDirectoryName(item.Path ?? string.Empty) ?? string.Empty;
        return !string.IsNullOrEmpty(dir)
            && dirCounts.TryGetValue(dir, out var count)
            && count > 1;
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

    private void NudgeLibraryRootLegacy(string rootPath)
    {
        try
        {
            if (Directory.Exists(rootPath))
                Directory.SetLastWriteTimeUtc(rootPath, DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "PosterRotator: unable to nudge legacy library root {Root}", rootPath);
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
        var dataFolder = Plugin.Instance?.DataFolderPath;
        if (string.IsNullOrWhiteSpace(dataFolder))
            return;

        var poolRoot = Path.Combine(dataFolder, PluginPoolDirectoryName);
        if (!Directory.Exists(poolRoot))
            return;

        foreach (var poolDir in Directory.GetDirectories(poolRoot))
            TryDeleteSafeDirectory(poolDir, poolRoot, requireLegacyPoolName: false, result);
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
            string[] children;

            try
            {
                children = Directory.GetDirectories(current);
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
