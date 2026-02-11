namespace Jellyfin.Plugin.PosterRotator.Services;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.PosterRotator.Helpers;
using Jellyfin.Plugin.PosterRotator.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

/// <summary>
/// Service pour gérer les pools d'images du plugin Poster Rotator.
/// </summary>
public class PoolService
{
    private readonly ILibraryManager _library;
    private readonly IServiceProvider _services;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<PoolService> _log;

    private static readonly string[] ImageExtensions = PluginHelpers.ImageExtensions;

    // P4: Cache pools for 10s to avoid double-scan (stats + list)
    private List<PoolInfo>? _cachedPools;
    private DateTime _cacheExpiry = DateTime.MinValue;
    private readonly object _cacheLock = new();

    public PoolService(
        ILibraryManager library,
        IServiceProvider services,
        IHttpClientFactory httpFactory,
        ILogger<PoolService> log)
    {
        _library = library;
        _services = services;
        _httpFactory = httpFactory;
        _log = log;
    }

    /// <summary>
    /// Récupère les statistiques globales de tous les pools.
    /// </summary>
    public PoolStatistics GetStatistics()
    {
        var stats = new PoolStatistics();
        var pools = GetAllPools();

        stats.TotalPools = pools.Count;
        stats.TotalImages = pools.Sum(p => p.Images.Count);
        stats.TotalSizeBytes = pools.Sum(p => p.TotalSizeBytes);
        stats.TotalSizeFormatted = PluginHelpers.FormatSize(stats.TotalSizeBytes);
        stats.LockedPools = pools.Count(p => p.IsLocked);
        stats.AverageImagesPerPool = stats.TotalPools > 0
            ? Math.Round((double)stats.TotalImages / stats.TotalPools, 1)
            : 0;

        var allItemIds = pools.Select(p => p.ItemId).ToHashSet();
        var mediaIds = GetAllMediaItemIds();
        stats.OrphanedPools = pools.Count(p => !mediaIds.Contains(p.ItemId));

        // Répartition par type
        stats.TypeBreakdown = new PoolTypeBreakdown
        {
            Movies = pools.Count(p => p.ItemType == "Movie"),
            Series = pools.Count(p => p.ItemType == "Series"),
            Seasons = pools.Count(p => p.ItemType == "Season"),
            Episodes = pools.Count(p => p.ItemType == "Episode"),
            BoxSets = pools.Count(p => p.ItemType == "BoxSet")
        };

        // Dernière rotation
        var lastRotation = pools
            .Where(p => p.LastRotation.HasValue)
            .OrderByDescending(p => p.LastRotation)
            .FirstOrDefault();
        stats.LastRotationTime = lastRotation?.LastRotation;

        // Rotations récentes
        var now = DateTimeOffset.UtcNow;
        stats.RotationsLast24h = pools.Count(p =>
            p.LastRotation.HasValue && (now - p.LastRotation.Value).TotalHours <= 24);
        stats.RotationsLast7d = pools.Count(p =>
            p.LastRotation.HasValue && (now - p.LastRotation.Value).TotalDays <= 7);

        return stats;
    }

    /// <summary>
    /// Récupère tous les pools existants.
    /// </summary>
    public List<PoolInfo> GetAllPools(CancellationToken ct = default)
    {
        // P4: Return cached result if still fresh
        lock (_cacheLock)
        {
            if (_cachedPools != null && DateTime.UtcNow < _cacheExpiry)
                return _cachedPools;
        }

        var pools = new List<PoolInfo>();
        var items = GetAllMediaItems();

        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();

            var poolInfo = GetPoolInfoForItem(item);
            if (poolInfo != null)
            {
                pools.Add(poolInfo);
            }
        }

        lock (_cacheLock)
        {
            _cachedPools = pools;
            _cacheExpiry = DateTime.UtcNow.AddSeconds(10);
        }

        return pools;
    }

    /// <summary>
    /// Invalidates the pool cache (called after mutations).
    /// </summary>
    private void InvalidateCache()
    {
        lock (_cacheLock)
        {
            _cachedPools = null;
            _cacheExpiry = DateTime.MinValue;
        }
    }

    /// <summary>
    /// Récupère le pool d'un item spécifique.
    /// </summary>
    public PoolInfo? GetPoolForItem(Guid itemId)
    {
        var item = _library.GetItemById(itemId);
        if (item == null)
        {
            _log.LogWarning("PoolService: Item {ItemId} not found", itemId);
            return null;
        }

        return GetPoolInfoForItem(item);
    }

    /// <summary>
    /// Ajoute une image au pool d'un item.
    /// </summary>
    public async Task<bool> AddImageToPoolAsync(Guid itemId, Stream imageStream, string fileName, CancellationToken ct = default)
    {
        var item = _library.GetItemById(itemId);
        if (item == null)
        {
            _log.LogWarning("PoolService: Cannot add image - Item {ItemId} not found", itemId);
            return false;
        }

        var poolDir = GetPoolDirectory(item);
        if (string.IsNullOrEmpty(poolDir))
        {
            return false;
        }

        Directory.CreateDirectory(poolDir);

        var ext = Path.GetExtension(fileName);
        if (string.IsNullOrEmpty(ext)) ext = ".jpg";
        var newFileName = $"pool_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}{ext}";
        var destPath = Path.Combine(poolDir, newFileName);

        try
        {
            using var fs = new FileStream(destPath, FileMode.Create, FileAccess.Write);
            await imageStream.CopyToAsync(fs, ct).ConfigureAwait(false);
            _log.LogInformation("PoolService: Added image {FileName} to pool for {Item}", newFileName, item.Name);
            InvalidateCache();
            return true;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "PoolService: Failed to add image to pool for {Item}", item.Name);
            return false;
        }
    }

    /// <summary>
    /// Supprime une image du pool d'un item.
    /// </summary>
    public bool DeleteImageFromPool(Guid itemId, string fileName)
    {
        var item = _library.GetItemById(itemId);
        if (item == null)
        {
            _log.LogWarning("PoolService: Cannot delete image - Item {ItemId} not found", itemId);
            return false;
        }

        var poolDir = GetPoolDirectory(item);
        if (string.IsNullOrEmpty(poolDir) || !Directory.Exists(poolDir))
        {
            return false;
        }

        var filePath = Path.Combine(poolDir, fileName);

        // R3: Normalize paths to prevent path traversal
        var normalizedPool = Path.GetFullPath(poolDir);
        var normalizedFile = Path.GetFullPath(filePath);
        if (!normalizedFile.StartsWith(normalizedPool, StringComparison.OrdinalIgnoreCase))
        {
            _log.LogWarning("PoolService: Attempted path traversal attack: {Path}", fileName);
            return false;
        }

        if (!File.Exists(filePath))
        {
            _log.LogWarning("PoolService: Image {FileName} not found in pool for {Item}", fileName, item.Name);
            return false;
        }

        try
        {
            File.Delete(filePath);
            InvalidateCache();
            _log.LogInformation("PoolService: Deleted image {FileName} from pool for {Item}", fileName, item.Name);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "PoolService: Failed to delete image {FileName} from pool for {Item}", fileName, item.Name);
            return false;
        }
    }

    /// <summary>
    /// Réordonne les images dans un pool.
    /// </summary>
    public bool ReorderPool(Guid itemId, List<string> orderedFileNames)
    {
        var item = _library.GetItemById(itemId);
        if (item == null)
        {
            return false;
        }

        var poolDir = GetPoolDirectory(item);
        if (string.IsNullOrEmpty(poolDir) || !Directory.Exists(poolDir))
        {
            return false;
        }

        var orderPath = Path.Combine(poolDir, "pool_order.json");
        try
        {
            var json = JsonSerializer.Serialize(orderedFileNames);
            File.WriteAllText(orderPath, json);
            _log.LogInformation("PoolService: Reordered pool for {Item} with {Count} images", item.Name, orderedFileNames.Count);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "PoolService: Failed to save pool order for {Item}", item.Name);
            return false;
        }
    }

    /// <summary>
    /// Nettoie les pools orphelins (médias supprimés).
    /// </summary>
    public int CleanupOrphanedPools(CancellationToken ct = default)
    {
        var pools = GetAllPools(ct);
        var allItemIds = GetAllMediaItemIds();
        var orphanedCount = 0;

        foreach (var pool in pools)
        {
            ct.ThrowIfCancellationRequested();

            if (!allItemIds.Contains(pool.ItemId) && Directory.Exists(pool.PoolPath))
            {
                try
                {
                    Directory.Delete(pool.PoolPath, recursive: true);
                    orphanedCount++;
                    _log.LogInformation("PoolService: Deleted orphaned pool at {Path}", pool.PoolPath);
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "PoolService: Failed to delete orphaned pool at {Path}", pool.PoolPath);
                }
            }
        }

        _log.LogInformation("PoolService: Cleanup completed - removed {Count} orphaned pools", orphanedCount);
        return orphanedCount;
    }

    /// <summary>
    /// Force la rotation immédiate d'un item vers la prochaine image du pool.
    /// Compatible avec le format rotation_state.json de PosterRotatorService.
    /// </summary>
    public async Task<bool> ForceRotateAsync(Guid itemId, CancellationToken ct = default)
    {
        var item = _library.GetItemById(itemId);
        if (item == null)
        {
            _log.LogWarning("PoolService: Cannot force rotate - Item {ItemId} not found", itemId);
            return false;
        }

        var poolDir = GetPoolDirectory(item);
        if (string.IsNullOrEmpty(poolDir) || !Directory.Exists(poolDir))
        {
            _log.LogWarning("PoolService: Cannot force rotate - No pool for {Item}", item.Name);
            return false;
        }

        var images = Directory.GetFiles(poolDir)
            .Where(f => ImageExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .OrderBy(f => Path.GetFileName(f).StartsWith("pool_currentprimary", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ThenBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (images.Count < 2)
        {
            _log.LogWarning("PoolService: Cannot force rotate - Not enough images for {Item}", item.Name);
            return false;
        }

        var statePath = Path.Combine(poolDir, "rotation_state.json");
        var state = PluginHelpers.LoadRotationState(statePath);
        var key = item.Id.ToString();

        int lastIdx = state.LastIndexByItem.TryGetValue(key, out var v) ? v : 0;
        int nextIdx = lastIdx % images.Count;

        var chosen = images[nextIdx];
        if (Path.GetFileName(chosen).StartsWith("pool_currentprimary", StringComparison.OrdinalIgnoreCase) && images.Count > 1)
        {
            nextIdx = (nextIdx + 1) % images.Count;
            chosen = images[nextIdx];
        }

        try
        {
            var currentPrimary = item.GetImagePath(ImageType.Primary);
            string destinationPath;

            if (!string.IsNullOrEmpty(currentPrimary))
            {
                destinationPath = currentPrimary;
            }
            else
            {
                var itemDir = GetItemDirectory(item);
                if (string.IsNullOrEmpty(itemDir)) return false;
                destinationPath = Path.Combine(itemDir, "poster" + Path.GetExtension(chosen));
            }

            // SafeOverwrite: temp file + rename
            var dir = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var tmp = destinationPath + ".tmp";
            File.Copy(chosen, tmp, overwrite: true);
            File.Move(tmp, destinationPath, overwrite: true);

            // Mettre à jour l'état
            state.LastIndexByItem[key] = nextIdx + 1;
            state.LastRotatedUtcByItem[key] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            PluginHelpers.SaveRotationState(statePath, state);

            // Notifier Jellyfin — properly awaited
            try
            {
                item.SetImagePath(ImageType.Primary, destinationPath);
                await item.UpdateToRepositoryAsync(ItemUpdateType.ImageUpdate, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "PoolService: Could not update repository for {Item}", item.Name);
            }

            _log.LogInformation("PoolService: Forced rotation for {Item} to {Image}", item.Name, Path.GetFileName(chosen));
            return true;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "PoolService: Failed to force rotate {Item}", item.Name);
            return false;
        }
    }

    /// <summary>
    /// Récupère le contenu binaire d'une image du pool.
    /// </summary>
    /// <summary>
    /// R2: Returns a stream instead of loading the entire file into memory.
    /// </summary>
    public (Stream? Data, string ContentType, long Length) GetPoolImage(Guid itemId, string fileName)
    {
        var empty = ((Stream?)null, "application/octet-stream", 0L);

        var item = _library.GetItemById(itemId);
        if (item == null) return empty;

        var poolDir = GetPoolDirectory(item);
        if (string.IsNullOrEmpty(poolDir)) return empty;

        var filePath = Path.Combine(poolDir, fileName);

        // R3: Normalize paths to prevent path traversal
        var normalizedPool = Path.GetFullPath(poolDir);
        var normalizedFile = Path.GetFullPath(filePath);
        if (!normalizedFile.StartsWith(normalizedPool, StringComparison.OrdinalIgnoreCase)) return empty;
        if (!File.Exists(filePath)) return empty;

        try
        {
            var fi = new FileInfo(filePath);
            var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var contentType = GetContentType(fileName);
            return (stream, contentType, fi.Length);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "PoolService: Failed to read pool image {FileName}", fileName);
            return empty;
        }
    }

    /// <summary>
    /// Recherche des images disponibles via les providers Jellyfin.
    /// Uses DI to resolve IRemoteImageProvider instances — no reflection.
    /// </summary>
    public async Task<List<Api.RemoteImageResult>?> SearchRemoteImagesAsync(Guid itemId, CancellationToken ct = default)
    {
        var item = _library.GetItemById(itemId);
        if (item == null)
        {
            return null;
        }

        var results = new List<Api.RemoteImageResult>();

        try
        {
            var providers = (_services.GetService(typeof(IEnumerable<IRemoteImageProvider>))
                            as IEnumerable<IRemoteImageProvider>)
                           ?? Array.Empty<IRemoteImageProvider>();

            foreach (var provider in providers)
            {
                try
                {
                    bool supports = true;
                    try { supports = provider.Supports(item); } catch { }
                    if (!supports) continue;

                    var images = await provider.GetImages(item, ct).ConfigureAwait(false);

                    foreach (var img in images)
                    {
                        if (img.Type != ImageType.Primary) continue;
                        if (string.IsNullOrEmpty(img.Url)) continue;

                        results.Add(new Api.RemoteImageResult
                        {
                            Url = img.Url,
                            ProviderName = img.ProviderName ?? provider.GetType().Name,
                            Language = img.Language,
                            Width = img.Width > 0 ? img.Width : null,
                            Height = img.Height > 0 ? img.Height : null,
                            ThumbnailUrl = img.ThumbnailUrl ?? img.Url
                        });
                    }
                }
                catch (Exception ex)
                {
                    _log.LogDebug(ex, "PoolService: Provider {Provider} failed for {Item}", provider.GetType().Name, item.Name);
                }
            }

            _log.LogDebug("PoolService: Found {Count} remote images for {Item}", results.Count, item.Name);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "PoolService: Failed to search remote images for {Item}", item.Name);
        }

        return results;
    }

    /// <summary>
    /// Télécharge une image depuis une URL et l'ajoute au pool.
    /// Uses IHttpClientFactory — no socket leak.
    /// </summary>
    public async Task<bool> AddImageFromUrlAsync(Guid itemId, string url, CancellationToken ct = default)
    {
        var item = _library.GetItemById(itemId);
        if (item == null || string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        var poolDir = GetPoolDirectory(item);
        if (string.IsNullOrEmpty(poolDir))
        {
            return false;
        }

        Directory.CreateDirectory(poolDir);

        try
        {
            using var http = _httpFactory.CreateClient("PosterRotator");
            using var response = await http.GetAsync(url, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var ext = PluginHelpers.GuessExtFromUrl(url) ?? ".jpg";
            var fileName = $"pool_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}{ext}";
            var destPath = Path.Combine(poolDir, fileName);

            using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var fs = new FileStream(destPath, FileMode.Create, FileAccess.Write);
            await stream.CopyToAsync(fs, ct).ConfigureAwait(false);

            _log.LogInformation("PoolService: Downloaded image from URL to {Path}", destPath);
            InvalidateCache();
            return true;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "PoolService: Failed to download image from URL for {Item}", item.Name);
            return false;
        }
    }

    // Q8: Thin async wrappers for controller compatibility.
    // Underlying operations are sync or already async.

    public Task<PoolStatistics> GetStatisticsAsync(CancellationToken ct = default)
        => Task.FromResult(GetStatistics());

    public Task<List<PoolInfo>> GetAllPoolsAsync(CancellationToken ct = default)
        => Task.FromResult(GetAllPools(ct));

    public Task<PoolInfo?> GetPoolForItemAsync(Guid itemId, CancellationToken ct = default)
        => Task.FromResult(GetPoolForItem(itemId));

    public Task<bool> DeleteImageFromPoolAsync(Guid itemId, string fileName, CancellationToken ct = default)
        => Task.FromResult(DeleteImageFromPool(itemId, fileName));

    public Task<bool> ReorderPoolAsync(Guid itemId, List<string> orderedFileNames, CancellationToken ct = default)
        => Task.FromResult(ReorderPool(itemId, orderedFileNames));

    public Task<int> CleanupOrphanedPoolsAsync(CancellationToken ct = default)
        => Task.FromResult(CleanupOrphanedPools(ct));

    public Task<(Stream? Data, string ContentType, long Length)> GetPoolImageAsync(Guid itemId, string fileName, CancellationToken ct = default)
        => Task.FromResult(GetPoolImage(itemId, fileName));

    // ── Private helpers ──────────────────────────────────────────────

    private PoolInfo? GetPoolInfoForItem(BaseItem item)
    {
        var poolDir = GetPoolDirectory(item);
        if (string.IsNullOrEmpty(poolDir) || !Directory.Exists(poolDir))
        {
            return null;
        }

        var images = new List<PoolImage>();
        long totalSize = 0;

        // Charger l'ordre personnalisé
        var orderPath = Path.Combine(poolDir, "pool_order.json");
        List<string>? customOrder = null;
        if (File.Exists(orderPath))
        {
            try
            {
                var json = File.ReadAllText(orderPath);
                customOrder = JsonSerializer.Deserialize<List<string>>(json);
            }
            catch { }
        }

        var files = Directory.GetFiles(poolDir)
            .Where(f => ImageExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .ToList();

        var currentPrimary = item.GetImagePath(ImageType.Primary);
        long currentPrimarySize = 0;
        if (!string.IsNullOrEmpty(currentPrimary) && File.Exists(currentPrimary))
        {
            try { currentPrimarySize = new FileInfo(currentPrimary).Length; } catch { }
        }

        var order = 0;
        foreach (var file in files)
        {
            var fi = new FileInfo(file);
            var fileName = fi.Name;
            var isCurrent = currentPrimarySize > 0 && fi.Length == currentPrimarySize;

            images.Add(new PoolImage
            {
                FileName = fileName,
                Url = $"/PosterRotator/Pool/{item.Id}/Image/{fileName}",
                SizeBytes = fi.Length,
                SizeFormatted = PluginHelpers.FormatSize(fi.Length),
                CreatedAt = fi.CreationTimeUtc,
                IsCurrent = isCurrent,
                Order = customOrder?.IndexOf(fileName) ?? order
            });

            totalSize += fi.Length;
            order++;
        }

        if (customOrder != null)
        {
            images = images.OrderBy(i => i.Order < 0 ? int.MaxValue : i.Order).ToList();
        }

        DateTimeOffset? lastRotation = null;
        var statePath = Path.Combine(poolDir, "rotation_state.json");
        var rotState = PluginHelpers.LoadRotationState(statePath);
        if (rotState.LastRotatedUtcByItem.TryGetValue(item.Id.ToString(), out var epoch))
        {
            lastRotation = DateTimeOffset.FromUnixTimeSeconds(epoch);
        }

        return new PoolInfo
        {
            ItemId = item.Id,
            ItemName = item.Name,
            ItemType = item.GetType().Name,
            PoolPath = poolDir,
            Images = images,
            TotalSizeBytes = totalSize,
            TotalSizeFormatted = PluginHelpers.FormatSize(totalSize),
            IsLocked = File.Exists(Path.Combine(poolDir, "pool.lock")),
            LastRotation = lastRotation
        };
    }

    private string? GetPoolDirectory(BaseItem item)
    {
        var itemDir = GetItemDirectory(item);
        return string.IsNullOrEmpty(itemDir) ? null : Path.Combine(itemDir, ".poster_pool");
    }

    private string? GetItemDirectory(BaseItem item)
        => PluginHelpers.GetItemDirectory(item.Path);

    /// <summary>
    /// Direct call to ILibraryManager.GetItemList — no reflection.
    /// </summary>
    private List<BaseItem> GetAllMediaItems()
    {
        try
        {
            var query = new InternalItemsQuery
            {
                IncludeItemTypes = new[]
                {
                    BaseItemKind.Movie,
                    BaseItemKind.Series,
                    BaseItemKind.Season,
                    BaseItemKind.Episode,
                    BaseItemKind.BoxSet
                },
                Recursive = true
            };

            return _library.GetItemList(query).ToList();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "PoolService: Failed to get media items");
        }

        return new List<BaseItem>();
    }

    private HashSet<Guid> GetAllMediaItemIds() => GetAllMediaItems().Select(i => i.Id).ToHashSet();

    private static string FormatSize(long bytes) => PluginHelpers.FormatSize(bytes);

    private static string GetContentType(string fileName) => PluginHelpers.GetContentType(fileName);
}
