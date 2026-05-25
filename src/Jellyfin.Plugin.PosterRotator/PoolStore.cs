using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PosterRotator.Helpers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PosterRotator;

public sealed class PoolStore
{
    private const int CurrentSchemaVersion = 1;
    private const int DeferredIndexFlushThreshold = 500;
    internal const string PoolRootDirectoryName = "Jellyfin.Plugin.PosterRotator.pools";
    private const string IndexFileName = "index.json";
    private const string PoolFileName = "pool.json";
    private static readonly TimeSpan DeferredIndexFlushInterval = TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _poolLocks = new();
    private readonly SemaphoreSlim _indexLock = new(1, 1);
    private readonly string? _dataFolderOverride;
    private readonly ILogger<PoolStore>? _log;
    private PoolIndexDocument? _deferredIndex;
    private DateTimeOffset _deferredIndexLastFlushUtc;
    private int _deferredIndexDepth;
    private int _deferredIndexPendingWrites;

    public PoolStore(ILogger<PoolStore> log)
    {
        _log = log;
    }

    internal PoolStore(string dataFolderPath)
    {
        _dataFolderOverride = dataFolderPath;
    }

    public async Task<IDisposable> LockPoolAsync(Guid itemId, CancellationToken cancellationToken)
    {
        var semaphore = _poolLocks.GetOrAdd(itemId, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Releaser(semaphore);
    }

    public string? TryGetPoolRootPath(bool create)
    {
        var dataFolder = _dataFolderOverride;
        if (string.IsNullOrWhiteSpace(dataFolder))
            dataFolder = GetPluginDataFolderPath();

        if (string.IsNullOrWhiteSpace(dataFolder))
            return null;

        var dataFolderFullPath = Path.GetFullPath(dataFolder);
        var parent = Path.GetDirectoryName(dataFolderFullPath);
        if (string.IsNullOrWhiteSpace(parent))
            return null;

        var root = Path.GetFullPath(Path.Combine(parent, PoolRootDirectoryName));
        if (create)
            Directory.CreateDirectory(root);

        return root;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string? GetPluginDataFolderPath() => Plugin.Instance?.DataFolderPath;

    public string? TryGetPoolDirectory(Guid itemId, bool create)
    {
        var root = TryGetPoolRootPath(create);
        if (root == null)
            return null;

        var directory = Path.GetFullPath(Path.Combine(root, itemId.ToString("N")));
        if (!PluginHelpers.IsPathInsideOrEqual(directory, root))
            return null;

        if (create)
            Directory.CreateDirectory(directory);

        return directory;
    }

    public string? TryCreatePoolDirectoryForWrite(Guid itemId)
    {
        var root = TryGetPoolRootPath(create: true);
        if (root == null || IsReparsePoint(root))
            return null;

        var directory = Path.GetFullPath(Path.Combine(root, itemId.ToString("N")));
        if (!PluginHelpers.IsPathInsideOrEqual(directory, root))
            return null;

        if (Directory.Exists(directory))
        {
            if (IsReparsePoint(directory))
                return null;
        }
        else
        {
            Directory.CreateDirectory(directory);
        }

        return directory;
    }

    public async Task<IAsyncDisposable> BeginDeferredIndexWritesAsync(CancellationToken cancellationToken)
    {
        await _indexLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_deferredIndexDepth == 0)
            {
                _deferredIndex = await LoadIndexAsync(cancellationToken).ConfigureAwait(false);
                _deferredIndexPendingWrites = 0;
                _deferredIndexLastFlushUtc = DateTimeOffset.UtcNow;
            }

            _deferredIndexDepth++;
            return new DeferredIndexScope(this);
        }
        finally
        {
            _indexLock.Release();
        }
    }

    public async Task<PoolMetadata> EnsurePoolAsync(PoolItemSnapshot item, string poolDir, CancellationToken cancellationToken)
    {
        var metadata = await LoadOrCreateMetadataAsync(item, poolDir, reconcileFiles: true, cancellationToken).ConfigureAwait(false);
        await SavePoolAsync(metadata, poolDir, cancellationToken).ConfigureAwait(false);
        return metadata;
    }

    public async Task<PoolMetadata?> GetPoolAsync(Guid itemId, bool reconcileFiles, CancellationToken cancellationToken)
    {
        var poolDir = TryGetPoolDirectory(itemId, create: false);
        if (poolDir == null || !Directory.Exists(poolDir))
            return null;

        var snapshot = new PoolItemSnapshot(itemId, string.Empty, string.Empty, string.Empty, null);
        var metadata = await LoadOrCreateMetadataAsync(snapshot, poolDir, reconcileFiles, cancellationToken).ConfigureAwait(false);
        if (reconcileFiles)
            await SavePoolAsync(metadata, poolDir, cancellationToken).ConfigureAwait(false);

        return metadata;
    }

    public async Task<IReadOnlyCollection<string>> GetKnownSourceUrlsAsync(
        PoolItemSnapshot item,
        string poolDir,
        CancellationToken cancellationToken)
    {
        var metadata = await LoadOrCreateMetadataAsync(item, poolDir, reconcileFiles: true, cancellationToken).ConfigureAwait(false);
        return metadata.Images
            .Select(image => image.SourceUrl)
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public async Task<int> CountLanguageImagesAsync(
        PoolItemSnapshot item,
        string poolDir,
        string language,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(language))
            return 0;

        var metadata = await LoadOrCreateMetadataAsync(item, poolDir, reconcileFiles: true, cancellationToken).ConfigureAwait(false);
        return metadata.Images.Count(image => image.Language.Equals(language, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<IReadOnlyCollection<ulong>> GetImageHashesAsync(
        PoolItemSnapshot item,
        string poolDir,
        CancellationToken cancellationToken)
    {
        var metadata = await LoadOrCreateMetadataAsync(item, poolDir, reconcileFiles: true, cancellationToken).ConfigureAwait(false);
        return metadata.Images.Select(image => image.Hash).Where(hash => hash != 0).ToArray();
    }

    public async Task RecordImageAsync(
        PoolItemSnapshot item,
        string poolDir,
        string imagePath,
        string source,
        string? language,
        string? sourceUrl,
        string? mimeType,
        int width,
        int height,
        ulong hash,
        CancellationToken cancellationToken)
    {
        var metadata = await LoadOrCreateMetadataAsync(item, poolDir, reconcileFiles: true, cancellationToken).ConfigureAwait(false);
        UpdateSnapshot(metadata, item);

        var fileName = Path.GetFileName(imagePath);
        var existing = metadata.Images.FirstOrDefault(image => image.FileName.Equals(fileName, StringComparison.OrdinalIgnoreCase));
        if (existing == null)
        {
            existing = new PoolImageMetadata { FileName = fileName, AddedUtc = DateTimeOffset.UtcNow };
            metadata.Images.Add(existing);
        }

        var info = new FileInfo(imagePath);
        existing.SizeBytes = info.Exists ? info.Length : 0;
        existing.Width = width;
        existing.Height = height;
        existing.MimeType = string.IsNullOrWhiteSpace(mimeType) ? PluginHelpers.GetContentType(fileName) : mimeType;
        existing.Language = string.IsNullOrWhiteSpace(language) ? "unknown" : language;
        existing.Source = string.IsNullOrWhiteSpace(source) ? "unknown" : source;
        existing.SourceUrl = sourceUrl;
        existing.Hash = hash;
        metadata.UpdatedUtc = DateTimeOffset.UtcNow;

        await SavePoolAsync(metadata, poolDir, cancellationToken).ConfigureAwait(false);
    }

    public async Task RecordRotationAsync(
        PoolItemSnapshot item,
        string poolDir,
        string imagePath,
        int nextIndex,
        DateTimeOffset rotatedUtc,
        CancellationToken cancellationToken)
    {
        var metadata = await LoadOrCreateMetadataAsync(item, poolDir, reconcileFiles: true, cancellationToken).ConfigureAwait(false);
        UpdateSnapshot(metadata, item);

        metadata.LastRotatedUtc = rotatedUtc;
        metadata.LastIndex = Math.Max(0, nextIndex);
        metadata.UpdatedUtc = rotatedUtc;

        var fileName = Path.GetFileName(imagePath);
        var image = metadata.Images.FirstOrDefault(entry => entry.FileName.Equals(fileName, StringComparison.OrdinalIgnoreCase));
        if (image != null)
            image.LastAppliedUtc = rotatedUtc;

        await SavePoolAsync(metadata, poolDir, cancellationToken).ConfigureAwait(false);
    }

    public async Task RecordErrorAsync(Guid itemId, string message, CancellationToken cancellationToken)
    {
        var poolDir = TryGetPoolDirectory(itemId, create: false);
        if (poolDir == null || !Directory.Exists(poolDir))
            return;

        var metadata = await GetPoolAsync(itemId, reconcileFiles: false, cancellationToken).ConfigureAwait(false);
        if (metadata == null)
            return;

        AddError(metadata, message);
        await SavePoolAsync(metadata, poolDir, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PoolImageMetadata> ImportImageAsync(
        PoolItemSnapshot item,
        Stream source,
        string originalFileName,
        Configuration cfg,
        CancellationToken cancellationToken)
    {
        var originalExtension = Path.GetExtension(originalFileName);
        if (!IsSupportedExtension(originalExtension))
            throw new InvalidDataException("Unsupported image extension.");

        var poolDir = TryGetPoolDirectory(item.ItemId, create: false)
            ?? throw new InvalidOperationException("Plugin data folder is unavailable.");
        var createdPoolDir = !Directory.Exists(poolDir);
        if (createdPoolDir)
            Directory.CreateDirectory(poolDir);

        var maxBytes = Math.Clamp(cfg.MaxDownloadMegabytes, 1, 200) * 1024L * 1024L;
        var baseName = $"upload_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}_{Guid.NewGuid():N}";
        var tmpPath = Path.Combine(poolDir, baseName + ".tmp");

        try
        {
            await PluginHelpers.CopyToFileWithLimitAsync(source, tmpPath, maxBytes, cancellationToken).ConfigureAwait(false);

            if (!PluginHelpers.TryGetImageFormat(tmpPath, out var extension, out var mimeType))
                throw new InvalidDataException("Unsupported image header.");

            if (!IsSupportedExtension(extension))
                throw new InvalidDataException("Unsupported image format.");

            var (width, height) = PluginHelpers.GetImageDimensions(tmpPath);
            if (width > 0 && height > 0 && (width < cfg.MinImageWidth || height < cfg.MinImageHeight))
                throw new InvalidDataException("Image dimensions are below the configured minimum.");

            var hash = cfg.EnableDuplicateDetection ? ImageHash.ComputeHash(tmpPath) : 0;
            if (cfg.EnableDuplicateDetection && hash != 0)
            {
                var existingHashes = await GetImageHashesAsync(item, poolDir, cancellationToken).ConfigureAwait(false);
                if (ImageHash.IsDuplicate(hash, existingHashes))
                    throw new InvalidDataException("Duplicate image.");
            }

            var finalPath = Path.Combine(poolDir, baseName + extension);
            File.Move(tmpPath, finalPath);

            await RecordImageAsync(
                item,
                poolDir,
                finalPath,
                source: "upload",
                language: "unknown",
                sourceUrl: null,
                mimeType,
                width,
                height,
                hash,
                cancellationToken).ConfigureAwait(false);

            var metadata = await GetPoolAsync(item.ItemId, reconcileFiles: false, cancellationToken).ConfigureAwait(false)
                ?? throw new IOException("Unable to reload pool metadata.");
            return metadata.Images.First(image => image.FileName.Equals(Path.GetFileName(finalPath), StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            TryDeleteFile(tmpPath);
            if (createdPoolDir && IsDirectoryEmpty(poolDir))
                TryDeleteDirectory(poolDir);
        }
    }

    public async Task<PoolImageMetadata> DeleteImageAsync(Guid itemId, string fileName, CancellationToken cancellationToken)
    {
        var poolDir = TryGetPoolDirectory(itemId, create: false)
            ?? throw new InvalidOperationException("Plugin data folder is unavailable.");
        var imagePath = ResolveImagePath(poolDir, fileName, requireExists: true);

        var metadata = await GetPoolAsync(itemId, reconcileFiles: true, cancellationToken).ConfigureAwait(false)
            ?? throw new FileNotFoundException("Pool not found.");
        var image = metadata.Images.FirstOrDefault(entry => entry.FileName.Equals(Path.GetFileName(imagePath), StringComparison.OrdinalIgnoreCase))
            ?? throw new FileNotFoundException("Image not found.");

        File.Delete(imagePath);
        metadata.Images.Remove(image);
        metadata.UpdatedUtc = DateTimeOffset.UtcNow;
        if (metadata.Images.Count == 0 && !ContainsReparsePoint(poolDir))
        {
            Directory.Delete(poolDir, recursive: true);
            await RemoveFromIndexAsync(metadata.ItemId, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await SavePoolAsync(metadata, poolDir, cancellationToken).ConfigureAwait(false);
        }

        return image;
    }

    public async Task<PoolImageFile> GetImageFileAsync(Guid itemId, string fileName, CancellationToken cancellationToken)
    {
        var poolDir = TryGetPoolDirectory(itemId, create: false)
            ?? throw new InvalidOperationException("Plugin data folder is unavailable.");
        var imagePath = ResolveImagePath(poolDir, fileName, requireExists: true);

        var metadata = await GetPoolAsync(itemId, reconcileFiles: false, cancellationToken).ConfigureAwait(false);
        var image = metadata?.Images.FirstOrDefault(entry => entry.FileName.Equals(Path.GetFileName(imagePath), StringComparison.OrdinalIgnoreCase));
        var contentType = image?.MimeType;
        if (string.IsNullOrWhiteSpace(contentType))
            contentType = PluginHelpers.GetContentType(fileName);

        return new PoolImageFile(imagePath, contentType);
    }

    public async Task<PoolListResponse> ListPoolsAsync(PoolListQuery query, CancellationToken cancellationToken)
    {
        var index = await LoadIndexAsync(cancellationToken).ConfigureAwait(false);
        var items = index.Pools.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(query.Library))
            items = items.Where(entry => entry.LibraryName.Equals(query.Library, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(query.Type))
            items = items.Where(entry => entry.ItemType.Equals(query.Type, StringComparison.OrdinalIgnoreCase));

        if (query.HasErrors.HasValue)
            items = items.Where(entry => entry.HasErrors == query.HasErrors.Value);

        if (query.IsEmpty.HasValue)
            items = items.Where(entry => (entry.ImageCount == 0) == query.IsEmpty.Value);

        if (!string.IsNullOrWhiteSpace(query.Query))
        {
            var text = query.Query.Trim();
            items = items.Where(entry =>
                entry.ItemName.Contains(text, StringComparison.OrdinalIgnoreCase)
                || entry.LibraryName.Contains(text, StringComparison.OrdinalIgnoreCase)
                || entry.ItemId.Contains(text, StringComparison.OrdinalIgnoreCase));
        }

        var ordered = items
            .OrderByDescending(entry => entry.UpdatedUtc)
            .ThenBy(entry => entry.ItemName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var start = Math.Max(0, query.Start);
        var limit = Math.Clamp(query.Limit <= 0 ? 50 : query.Limit, 1, 200);
        return new PoolListResponse
        {
            Start = start,
            Limit = limit,
            Total = ordered.Count,
            Items = ordered.Skip(start).Take(limit).ToList()
        };
    }

    public async Task<IReadOnlyList<PoolIndexEntry>> GetIndexEntriesAsync(CancellationToken cancellationToken)
    {
        var index = await LoadIndexAsync(cancellationToken).ConfigureAwait(false);
        return index.Pools.ToList();
    }

    public async Task<PoolDiagnostics> GetDiagnosticsAsync(Func<Guid, bool> itemExists, CancellationToken cancellationToken)
    {
        var index = await LoadIndexAsync(cancellationToken).ConfigureAwait(false);
        return BuildDiagnostics(index, itemExists, cancellationToken);
    }

    public async Task<PoolDiagnostics> GetDiagnosticsAsync(IReadOnlySet<Guid> existingItemIds, CancellationToken cancellationToken)
    {
        var index = await LoadIndexAsync(cancellationToken).ConfigureAwait(false);
        return BuildDiagnostics(index, itemId => existingItemIds.Contains(itemId), cancellationToken);
    }

    private static PoolDiagnostics BuildDiagnostics(
        PoolIndexDocument index,
        Func<Guid, bool> itemExists,
        CancellationToken cancellationToken)
    {
        var orphanCount = 0;
        var errors = new List<PoolErrorInfo>();

        foreach (var entry in index.Pools)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Guid.TryParse(entry.ItemId, out var itemId) && !itemExists(itemId))
                orphanCount++;

            if (!string.IsNullOrWhiteSpace(entry.LastError))
            {
                errors.Add(new PoolErrorInfo
                {
                    TimestampUtc = entry.UpdatedUtc,
                    Message = entry.LastError,
                    ItemId = entry.ItemId,
                    ItemName = entry.ItemName
                });
            }
        }

        return new PoolDiagnostics
        {
            PoolCount = index.Pools.Count,
            TotalSizeBytes = index.Pools.Sum(entry => entry.SizeBytes),
            OrphanCount = orphanCount,
            RecentPools = index.Pools
                .OrderByDescending(entry => entry.UpdatedUtc)
                .Take(10)
                .ToList(),
            RecentErrors = errors
                .OrderByDescending(error => error.TimestampUtc)
                .Take(10)
                .ToList()
        };
    }

    public async Task<PoolRebuildIndexResult> RebuildIndexAsync(CancellationToken cancellationToken)
    {
        var result = new PoolRebuildIndexResult();
        var root = TryGetPoolRootPath(create: false);
        var index = new PoolIndexDocument();

        if (root == null || !Directory.Exists(root))
            return result;

        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out var itemId))
            {
                result.SkippedCount++;
                continue;
            }

            if (ContainsReparsePoint(directory))
            {
                result.SkippedCount++;
                continue;
            }

            try
            {
                var metadata = await LoadOrCreateMetadataAsync(
                    new PoolItemSnapshot(itemId, string.Empty, string.Empty, string.Empty, null),
                    directory,
                    reconcileFiles: true,
                    cancellationToken).ConfigureAwait(false);
                if (metadata.Images.Count == 0)
                {
                    result.SkippedCount++;
                    continue;
                }

                await SavePoolMetadataOnlyAsync(metadata, directory, cancellationToken).ConfigureAwait(false);
                index.Pools.Add(CreateIndexEntry(metadata));
                result.IndexedCount++;
            }
            catch (Exception ex)
            {
                result.FailedCount++;
                _log?.LogWarning(ex, "PosterRotator: failed to rebuild pool index for {PoolDir}", directory);
            }
        }

        await SaveIndexAsync(index, cancellationToken).ConfigureAwait(false);
        result.TotalCount = index.Pools.Count;
        return result;
    }

    public async Task<PurgePoolsResult> PurgeAsync(
        PoolPurgeRequest request,
        Func<Guid, bool> itemExists,
        CancellationToken cancellationToken)
    {
        var result = new PurgePoolsResult();
        var root = TryGetPoolRootPath(create: false);
        if (root == null || !Directory.Exists(root))
            return result;

        var index = await LoadIndexAsync(cancellationToken).ConfigureAwait(false);
        var scope = request.Scope?.Trim().ToLowerInvariant() ?? string.Empty;
        var targets = scope switch
        {
            "orphans" => index.Pools
                .Where(entry => Guid.TryParse(entry.ItemId, out var itemId) && !itemExists(itemId))
                .ToList(),
            "library" => index.Pools
                .Where(entry => !string.IsNullOrWhiteSpace(request.LibraryName)
                    && entry.LibraryName.Equals(request.LibraryName, StringComparison.OrdinalIgnoreCase))
                .ToList(),
            "item" => index.Pools
                .Where(entry => Guid.TryParse(entry.ItemId, out var itemId) && itemId == request.ItemId)
                .ToList(),
            _ => throw new ArgumentException("Unsupported purge scope.", nameof(request))
        };

        foreach (var entry in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Guid.TryParse(entry.ItemId, out var itemId))
            {
                result.SkippedCount++;
                continue;
            }

            using var poolLock = await LockPoolAsync(itemId, cancellationToken).ConfigureAwait(false);
            var poolDir = TryGetPoolDirectory(itemId, create: false);
            if (poolDir == null || !Directory.Exists(poolDir) || ContainsReparsePoint(poolDir))
            {
                result.SkippedCount++;
                continue;
            }

            try
            {
                Directory.Delete(poolDir, recursive: true);
                result.DeletedCount++;
                index.Pools.RemoveAll(pool => pool.ItemId.Equals(entry.ItemId, StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception ex)
            {
                result.FailedCount++;
                _log?.LogWarning(ex, "PosterRotator: failed to purge pool {PoolDir}", poolDir);
            }
        }

        await SaveIndexAsync(index, cancellationToken).ConfigureAwait(false);
        return result;
    }

    private async Task<PoolIndexDocument> LoadIndexAsync(CancellationToken cancellationToken)
    {
        var root = TryGetPoolRootPath(create: false);
        if (root == null)
            return new PoolIndexDocument();

        var indexPath = Path.Combine(root, IndexFileName);
        try
        {
            if (!File.Exists(indexPath))
                return new PoolIndexDocument();

            await using var stream = File.OpenRead(indexPath);
            var index = await JsonSerializer.DeserializeAsync<PoolIndexDocument>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
            return index ?? new PoolIndexDocument();
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "PosterRotator: unable to read pool index.");
            return new PoolIndexDocument();
        }
    }

    private async Task SaveIndexAsync(PoolIndexDocument index, CancellationToken cancellationToken)
    {
        await _indexLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SaveIndexUnsafeAsync(index, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _indexLock.Release();
        }
    }

    private async Task SaveIndexUnsafeAsync(PoolIndexDocument index, CancellationToken cancellationToken)
    {
        var root = TryGetPoolRootPath(create: true)
            ?? throw new InvalidOperationException("Plugin data folder is unavailable.");
        index.SchemaVersion = CurrentSchemaVersion;
        index.UpdatedUtc = DateTimeOffset.UtcNow;
        await WriteJsonAtomicAsync(Path.Combine(root, IndexFileName), index, cancellationToken).ConfigureAwait(false);
    }

    private async Task SavePoolAsync(PoolMetadata metadata, string poolDir, CancellationToken cancellationToken)
    {
        await SavePoolMetadataOnlyAsync(metadata, poolDir, cancellationToken).ConfigureAwait(false);
        await UpsertIndexAsync(metadata, cancellationToken).ConfigureAwait(false);
    }

    private async Task SavePoolMetadataOnlyAsync(PoolMetadata metadata, string poolDir, CancellationToken cancellationToken)
    {
        metadata.SchemaVersion = CurrentSchemaVersion;
        metadata.UpdatedUtc = DateTimeOffset.UtcNow;
        Recalculate(metadata);
        await WriteJsonAtomicAsync(Path.Combine(poolDir, PoolFileName), metadata, cancellationToken).ConfigureAwait(false);
    }

    private async Task UpsertIndexAsync(PoolMetadata metadata, CancellationToken cancellationToken)
    {
        await _indexLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var index = _deferredIndex ?? await LoadIndexAsync(cancellationToken).ConfigureAwait(false);
            var entry = CreateIndexEntry(metadata);
            var existing = index.Pools.FindIndex(pool => pool.ItemId.Equals(entry.ItemId, StringComparison.OrdinalIgnoreCase));
            if (existing >= 0)
                index.Pools[existing] = entry;
            else
                index.Pools.Add(entry);

            if (_deferredIndex != null)
            {
                _deferredIndexPendingWrites++;
                var now = DateTimeOffset.UtcNow;
                if (_deferredIndexPendingWrites >= DeferredIndexFlushThreshold
                    || now - _deferredIndexLastFlushUtc >= DeferredIndexFlushInterval)
                {
                    await SaveIndexUnsafeAsync(_deferredIndex, cancellationToken).ConfigureAwait(false);
                    _deferredIndexPendingWrites = 0;
                    _deferredIndexLastFlushUtc = now;
                }

                return;
            }

            await SaveIndexUnsafeAsync(index, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _indexLock.Release();
        }
    }

    private async Task RemoveFromIndexAsync(string itemId, CancellationToken cancellationToken)
    {
        await _indexLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var index = _deferredIndex ?? await LoadIndexAsync(cancellationToken).ConfigureAwait(false);
            var removed = index.Pools.RemoveAll(pool => pool.ItemId.Equals(itemId, StringComparison.OrdinalIgnoreCase));
            if (removed == 0)
                return;

            if (_deferredIndex != null)
            {
                _deferredIndexPendingWrites++;
                return;
            }

            await SaveIndexUnsafeAsync(index, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _indexLock.Release();
        }
    }

    private async ValueTask EndDeferredIndexWritesAsync()
    {
        await _indexLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (_deferredIndexDepth <= 0)
                return;

            _deferredIndexDepth--;
            if (_deferredIndexDepth > 0)
                return;

            if (_deferredIndex != null)
                await SaveIndexUnsafeAsync(_deferredIndex, CancellationToken.None).ConfigureAwait(false);

            _deferredIndex = null;
            _deferredIndexPendingWrites = 0;
            _deferredIndexLastFlushUtc = default;
        }
        finally
        {
            _indexLock.Release();
        }
    }

    private async Task<PoolMetadata> LoadOrCreateMetadataAsync(
        PoolItemSnapshot item,
        string poolDir,
        bool reconcileFiles,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(poolDir);
        var poolPath = Path.Combine(poolDir, PoolFileName);
        PoolMetadata? metadata = null;

        if (File.Exists(poolPath))
        {
            try
            {
                await using var stream = File.OpenRead(poolPath);
                metadata = await JsonSerializer.DeserializeAsync<PoolMetadata>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log?.LogWarning(ex, "PosterRotator: unable to read pool metadata {PoolPath}", poolPath);
            }
        }

        metadata ??= new PoolMetadata
        {
            ItemId = item.ItemId.ToString(),
            CreatedUtc = DateTimeOffset.UtcNow
        };

        UpdateSnapshot(metadata, item);

        if (reconcileFiles)
            ReconcileFiles(metadata, poolDir);

        return metadata;
    }

    private static void UpdateSnapshot(PoolMetadata metadata, PoolItemSnapshot item)
    {
        if (item.ItemId != Guid.Empty)
            metadata.ItemId = item.ItemId.ToString();
        if (!string.IsNullOrWhiteSpace(item.Name))
            metadata.ItemName = item.Name;
        if (!string.IsNullOrWhiteSpace(item.Type))
            metadata.ItemType = item.Type;
        if (!string.IsNullOrWhiteSpace(item.LibraryName))
            metadata.LibraryName = item.LibraryName;
        if (!string.IsNullOrWhiteSpace(item.Path))
            metadata.ItemPath = item.Path;
    }

    private static void ReconcileFiles(PoolMetadata metadata, string poolDir)
    {
        var files = EnumeratePoolImages(poolDir)
            .Select(file => Path.GetFileName(file))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        metadata.Images.RemoveAll(image => !files.Contains(image.FileName));

        foreach (var file in EnumeratePoolImages(poolDir))
        {
            var fileName = Path.GetFileName(file);
            if (metadata.Images.Any(image => image.FileName.Equals(fileName, StringComparison.OrdinalIgnoreCase)))
                continue;

            metadata.Images.Add(CreateImageMetadata(file, "discovered", "unknown", null, 0));
        }
    }

    private static PoolImageMetadata CreateImageMetadata(
        string filePath,
        string source,
        string language,
        string? sourceUrl,
        ulong hash)
    {
        PluginHelpers.TryGetImageFormat(filePath, out _, out var mimeType);
        var (width, height) = PluginHelpers.GetImageDimensions(filePath);
        var info = new FileInfo(filePath);
        return new PoolImageMetadata
        {
            FileName = Path.GetFileName(filePath),
            SizeBytes = info.Exists ? info.Length : 0,
            Width = width,
            Height = height,
            MimeType = mimeType,
            Language = string.IsNullOrWhiteSpace(language) ? "unknown" : language,
            Source = source,
            SourceUrl = sourceUrl,
            Hash = hash,
            AddedUtc = info.Exists ? info.CreationTimeUtc : DateTimeOffset.UtcNow
        };
    }

    private static void Recalculate(PoolMetadata metadata)
    {
        metadata.ImageCount = metadata.Images.Count;
        metadata.SizeBytes = metadata.Images.Sum(image => image.SizeBytes);
        metadata.LastError = metadata.RecentErrors
            .OrderByDescending(error => error.TimestampUtc)
            .FirstOrDefault()?.Message;
    }

    private static PoolIndexEntry CreateIndexEntry(PoolMetadata metadata)
    {
        Recalculate(metadata);
        return new PoolIndexEntry
        {
            ItemId = metadata.ItemId,
            ItemName = metadata.ItemName,
            ItemType = metadata.ItemType,
            LibraryName = metadata.LibraryName,
            ItemPath = metadata.ItemPath,
            ImageCount = metadata.ImageCount,
            SizeBytes = metadata.SizeBytes,
            LastRotatedUtc = metadata.LastRotatedUtc,
            UpdatedUtc = metadata.UpdatedUtc,
            LastError = metadata.LastError,
            HasErrors = !string.IsNullOrWhiteSpace(metadata.LastError)
        };
    }

    private static void AddError(PoolMetadata metadata, string message)
    {
        metadata.RecentErrors.Add(new PoolErrorInfo
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            Message = message,
            ItemId = metadata.ItemId,
            ItemName = metadata.ItemName
        });

        metadata.RecentErrors = metadata.RecentErrors
            .OrderByDescending(error => error.TimestampUtc)
            .Take(10)
            .ToList();
    }

    private static IEnumerable<string> EnumeratePoolImages(string poolDir)
    {
        if (!Directory.Exists(poolDir))
            return Array.Empty<string>();

        return Directory
            .EnumerateFiles(poolDir, "*", SearchOption.TopDirectoryOnly)
            .Where(file => IsSupportedExtension(Path.GetExtension(file)));
    }

    private static string ResolveImagePath(string poolDir, string fileName, bool requireExists)
    {
        if (string.IsNullOrWhiteSpace(fileName) || fileName != Path.GetFileName(fileName))
            throw new InvalidDataException("Invalid image file name.");

        if (fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidDataException("Invalid image file name.");

        if (!IsSupportedExtension(Path.GetExtension(fileName)))
            throw new InvalidDataException("Unsupported image extension.");

        var fullPoolDir = Path.GetFullPath(poolDir);
        var path = Path.GetFullPath(Path.Combine(fullPoolDir, fileName));
        if (!PluginHelpers.IsPathInsideOrEqual(path, fullPoolDir))
            throw new InvalidDataException("Image path escapes the pool directory.");

        if (requireExists && (!File.Exists(path) || IsReparsePoint(path)))
            throw new FileNotFoundException("Image not found.");

        return path;
    }

    private static bool IsSupportedExtension(string? extension) =>
        extension?.ToLowerInvariant() is ".jpg" or ".jpeg" or ".png" or ".webp" or ".gif";

    private static async Task WriteJsonAtomicAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + "." + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ".tmp";
        await using (var stream = File.Create(tmp))
        {
            await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken).ConfigureAwait(false);
        }

        File.Move(tmp, path, overwrite: true);
    }

    private static bool ContainsReparsePoint(string directory)
    {
        if (IsReparsePoint(directory))
            return true;

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

    private sealed class Releaser : IDisposable
    {
        private readonly SemaphoreSlim _semaphore;
        private bool _disposed;

        public Releaser(SemaphoreSlim semaphore)
        {
            _semaphore = semaphore;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _semaphore.Release();
        }
    }

    private sealed class DeferredIndexScope : IAsyncDisposable
    {
        private readonly PoolStore _store;
        private bool _disposed;

        public DeferredIndexScope(PoolStore store)
        {
            _store = store;
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
                return;

            _disposed = true;
            await _store.EndDeferredIndexWritesAsync().ConfigureAwait(false);
        }
    }
}

public sealed record PoolItemSnapshot(Guid ItemId, string Name, string Type, string LibraryName, string? Path);

public sealed record PoolImageFile(string Path, string ContentType);

public sealed class PoolMetadata
{
    public int SchemaVersion { get; set; } = 1;
    public string ItemId { get; set; } = string.Empty;
    public string ItemName { get; set; } = string.Empty;
    public string ItemType { get; set; } = string.Empty;
    public string LibraryName { get; set; } = string.Empty;
    public string? ItemPath { get; set; }
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastRotatedUtc { get; set; }
    public int LastIndex { get; set; }
    public int ImageCount { get; set; }
    public long SizeBytes { get; set; }
    public string? LastError { get; set; }
    public List<PoolImageMetadata> Images { get; set; } = new();
    public List<PoolErrorInfo> RecentErrors { get; set; } = new();
}

public sealed class PoolImageMetadata
{
    public string FileName { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public string MimeType { get; set; } = string.Empty;
    public string Language { get; set; } = "unknown";
    public string Source { get; set; } = "unknown";
    public string? SourceUrl { get; set; }
    public ulong Hash { get; set; }
    public DateTimeOffset AddedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastAppliedUtc { get; set; }
}

public sealed class PoolIndexDocument
{
    public int SchemaVersion { get; set; } = 1;
    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public List<PoolIndexEntry> Pools { get; set; } = new();
}

public sealed class PoolIndexEntry
{
    public string ItemId { get; set; } = string.Empty;
    public string ItemName { get; set; } = string.Empty;
    public string ItemType { get; set; } = string.Empty;
    public string LibraryName { get; set; } = string.Empty;
    public string? ItemPath { get; set; }
    public int ImageCount { get; set; }
    public long SizeBytes { get; set; }
    public DateTimeOffset? LastRotatedUtc { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; }
    public bool HasErrors { get; set; }
    public string? LastError { get; set; }
}

public sealed class PoolErrorInfo
{
    public DateTimeOffset TimestampUtc { get; set; }
    public string Message { get; set; } = string.Empty;
    public string ItemId { get; set; } = string.Empty;
    public string ItemName { get; set; } = string.Empty;
}

public sealed class PoolListQuery
{
    public string? Library { get; set; }
    public string? Query { get; set; }
    public string? Type { get; set; }
    public bool? HasErrors { get; set; }
    public bool? IsEmpty { get; set; }
    public int Start { get; set; }
    public int Limit { get; set; } = 50;
}

public sealed class PoolListResponse
{
    public int Start { get; set; }
    public int Limit { get; set; }
    public int Total { get; set; }
    public List<PoolIndexEntry> Items { get; set; } = new();
}

public sealed class PoolDiagnostics
{
    public int PoolCount { get; set; }
    public long TotalSizeBytes { get; set; }
    public int OrphanCount { get; set; }
    public List<PoolIndexEntry> RecentPools { get; set; } = new();
    public List<PoolErrorInfo> RecentErrors { get; set; } = new();
}

public sealed class PoolPurgeRequest
{
    public string? Scope { get; set; }
    public string? LibraryName { get; set; }
    public Guid? ItemId { get; set; }
}

public sealed class PoolRebuildIndexResult
{
    public int TotalCount { get; set; }
    public int IndexedCount { get; set; }
    public int SkippedCount { get; set; }
    public int FailedCount { get; set; }
}

public sealed class PoolOperationResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public string? ItemId { get; set; }
    public string? FileName { get; set; }
    public int ProcessedCount { get; set; }
    public int RotatedCount { get; set; }
    public int FailedCount { get; set; }
}

public sealed class PoolDownloadResult
{
    public int CandidateCount { get; set; }
    public int ProcessedCount { get; set; }
    public int SkippedCount { get; set; }
    public int CompletedPoolCount { get; set; }
    public int ImagesAdded { get; set; }
    public int ErrorCount { get; set; }
    public int ProviderLookups { get; set; }
    public int DownloadAttempts { get; set; }
    public string Message { get; set; } = string.Empty;
}
