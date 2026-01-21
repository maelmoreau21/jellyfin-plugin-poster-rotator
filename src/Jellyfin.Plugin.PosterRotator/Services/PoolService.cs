namespace Jellyfin.Plugin.PosterRotator.Services;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.PosterRotator.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

/// <summary>
/// Service pour gérer les pools d'images du plugin Poster Rotator.
/// </summary>
public class PoolService
{
    private readonly ILibraryManager _library;
    private readonly ILogger<PoolService> _log;

    private static readonly string[] ImageExtensions = { ".jpg", ".jpeg", ".png", ".webp", ".gif" };

    public PoolService(ILibraryManager library, ILogger<PoolService> log)
    {
        _library = library;
        _log = log;
    }

    /// <summary>
    /// Récupère les statistiques globales de tous les pools.
    /// </summary>
    public async Task<PoolStatistics> GetStatisticsAsync(CancellationToken ct = default)
    {
        var stats = new PoolStatistics();
        var pools = await GetAllPoolsAsync(ct).ConfigureAwait(false);

        stats.TotalPools = pools.Count;
        stats.TotalImages = pools.Sum(p => p.Images.Count);
        stats.TotalSizeBytes = pools.Sum(p => p.TotalSizeBytes);
        stats.TotalSizeFormatted = FormatSize(stats.TotalSizeBytes);
        stats.LockedPools = pools.Count(p => p.IsLocked);
        stats.AverageImagesPerPool = stats.TotalPools > 0 
            ? Math.Round((double)stats.TotalImages / stats.TotalPools, 1) 
            : 0;

        // Calcul des pools orphelins
        var allItemIds = GetAllMediaItemIds();
        stats.OrphanedPools = pools.Count(p => !allItemIds.Contains(p.ItemId));

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

        // Rotations récentes (basé sur les timestamps des fichiers)
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
    public Task<List<PoolInfo>> GetAllPoolsAsync(CancellationToken ct = default)
    {
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

        return Task.FromResult(pools);
    }

    /// <summary>
    /// Récupère le pool d'un item spécifique.
    /// </summary>
    public Task<PoolInfo?> GetPoolForItemAsync(Guid itemId, CancellationToken ct = default)
    {
        var item = _library.GetItemById(itemId);
        if (item == null)
        {
            _log.LogWarning("PoolService: Item {ItemId} not found", itemId);
            return Task.FromResult<PoolInfo?>(null);
        }

        var poolInfo = GetPoolInfoForItem(item);
        return Task.FromResult(poolInfo);
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

        // Générer un nom de fichier unique
        var ext = Path.GetExtension(fileName);
        if (string.IsNullOrEmpty(ext)) ext = ".jpg";
        var newFileName = $"pool_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}{ext}";
        var destPath = Path.Combine(poolDir, newFileName);

        try
        {
            using var fs = new FileStream(destPath, FileMode.Create, FileAccess.Write);
            await imageStream.CopyToAsync(fs, ct).ConfigureAwait(false);
            _log.LogInformation("PoolService: Added image {FileName} to pool for {Item}", newFileName, item.Name);
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
    public Task<bool> DeleteImageFromPoolAsync(Guid itemId, string fileName, CancellationToken ct = default)
    {
        var item = _library.GetItemById(itemId);
        if (item == null)
        {
            _log.LogWarning("PoolService: Cannot delete image - Item {ItemId} not found", itemId);
            return Task.FromResult(false);
        }

        var poolDir = GetPoolDirectory(item);
        if (string.IsNullOrEmpty(poolDir) || !Directory.Exists(poolDir))
        {
            return Task.FromResult(false);
        }

        var filePath = Path.Combine(poolDir, fileName);
        
        // Sécurité : vérifier que le fichier est bien dans le pool
        if (!filePath.StartsWith(poolDir, StringComparison.OrdinalIgnoreCase))
        {
            _log.LogWarning("PoolService: Attempted path traversal attack: {Path}", fileName);
            return Task.FromResult(false);
        }

        if (!File.Exists(filePath))
        {
            _log.LogWarning("PoolService: Image {FileName} not found in pool for {Item}", fileName, item.Name);
            return Task.FromResult(false);
        }

        try
        {
            File.Delete(filePath);
            _log.LogInformation("PoolService: Deleted image {FileName} from pool for {Item}", fileName, item.Name);
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "PoolService: Failed to delete image {FileName} from pool for {Item}", fileName, item.Name);
            return Task.FromResult(false);
        }
    }

    /// <summary>
    /// Réordonne les images dans un pool.
    /// </summary>
    public Task<bool> ReorderPoolAsync(Guid itemId, List<string> orderedFileNames, CancellationToken ct = default)
    {
        var item = _library.GetItemById(itemId);
        if (item == null)
        {
            return Task.FromResult(false);
        }

        var poolDir = GetPoolDirectory(item);
        if (string.IsNullOrEmpty(poolDir) || !Directory.Exists(poolDir))
        {
            return Task.FromResult(false);
        }

        // Sauvegarder l'ordre dans un fichier JSON
        var orderPath = Path.Combine(poolDir, "pool_order.json");
        try
        {
            var json = JsonSerializer.Serialize(orderedFileNames);
            File.WriteAllText(orderPath, json);
            _log.LogInformation("PoolService: Reordered pool for {Item} with {Count} images", item.Name, orderedFileNames.Count);
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "PoolService: Failed to save pool order for {Item}", item.Name);
            return Task.FromResult(false);
        }
    }

    /// <summary>
    /// Nettoie les pools orphelins (médias supprimés).
    /// </summary>
    public async Task<int> CleanupOrphanedPoolsAsync(CancellationToken ct = default)
    {
        var pools = await GetAllPoolsAsync(ct).ConfigureAwait(false);
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
    /// Récupère le contenu binaire d'une image du pool.
    /// </summary>
    public Task<(byte[]? data, string? contentType)> GetPoolImageAsync(Guid itemId, string fileName, CancellationToken ct = default)
    {
        var item = _library.GetItemById(itemId);
        if (item == null)
        {
            return Task.FromResult<(byte[]?, string?)>((null, null));
        }

        var poolDir = GetPoolDirectory(item);
        if (string.IsNullOrEmpty(poolDir) || !Directory.Exists(poolDir))
        {
            return Task.FromResult<(byte[]?, string?)>((null, null));
        }

        var filePath = Path.Combine(poolDir, fileName);
        
        // Sécurité
        if (!filePath.StartsWith(poolDir, StringComparison.OrdinalIgnoreCase) || !File.Exists(filePath))
        {
            return Task.FromResult<(byte[]?, string?)>((null, null));
        }

        try
        {
            var data = File.ReadAllBytes(filePath);
            var contentType = GetContentType(fileName);
            return Task.FromResult<(byte[]?, string?)>((data, contentType));
        }
        catch
        {
            return Task.FromResult<(byte[]?, string?)>((null, null));
        }
    }

    #region Private Helpers

    private PoolInfo? GetPoolInfoForItem(BaseItem item)
    {
        var poolDir = GetPoolDirectory(item);
        if (string.IsNullOrEmpty(poolDir) || !Directory.Exists(poolDir))
        {
            return null;
        }

        var images = new List<PoolImage>();
        long totalSize = 0;

        // Charger l'ordre personnalisé si existant
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

        // Récupérer les images
        var files = Directory.GetFiles(poolDir)
            .Where(f => ImageExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .ToList();

        // Image actuelle
        var currentPrimary = item.GetImagePath(ImageType.Primary);
        var currentFileName = !string.IsNullOrEmpty(currentPrimary) 
            ? Path.GetFileName(currentPrimary) 
            : null;

        var order = 0;
        foreach (var file in files)
        {
            var fi = new FileInfo(file);
            var fileName = fi.Name;

            images.Add(new PoolImage
            {
                FileName = fileName,
                Url = $"/PosterRotator/Pool/{item.Id}/Image/{fileName}",
                SizeBytes = fi.Length,
                SizeFormatted = FormatSize(fi.Length),
                CreatedAt = fi.CreationTimeUtc,
                IsCurrent = fileName.Equals(currentFileName, StringComparison.OrdinalIgnoreCase) ||
                           fileName.StartsWith("pool_currentprimary", StringComparison.OrdinalIgnoreCase),
                Order = customOrder?.IndexOf(fileName) ?? order
            });

            totalSize += fi.Length;
            order++;
        }

        // Trier par ordre personnalisé si disponible
        if (customOrder != null)
        {
            images = images.OrderBy(i => i.Order < 0 ? int.MaxValue : i.Order).ToList();
        }

        // Charger l'état de rotation
        DateTimeOffset? lastRotation = null;
        var statePath = Path.Combine(poolDir, "rotation_state.json");
        if (File.Exists(statePath))
        {
            try
            {
                var json = File.ReadAllText(statePath);
                var state = JsonSerializer.Deserialize<RotationState>(json);
                if (state?.LastRotatedUtcByItem?.TryGetValue(item.Id.ToString(), out var epoch) == true)
                {
                    lastRotation = DateTimeOffset.FromUnixTimeSeconds(epoch);
                }
            }
            catch { }
        }

        return new PoolInfo
        {
            ItemId = item.Id,
            ItemName = item.Name,
            ItemType = item.GetType().Name,
            PoolPath = poolDir,
            Images = images,
            TotalSizeBytes = totalSize,
            TotalSizeFormatted = FormatSize(totalSize),
            IsLocked = File.Exists(Path.Combine(poolDir, "pool.lock")),
            LastRotation = lastRotation
        };
    }

    private string? GetPoolDirectory(BaseItem item)
    {
        var itemDir = GetItemDirectory(item);
        if (string.IsNullOrEmpty(itemDir))
        {
            return null;
        }

        return Path.Combine(itemDir, ".poster_pool");
    }

    private string? GetItemDirectory(BaseItem item)
    {
        if (string.IsNullOrEmpty(item.Path))
        {
            return null;
        }

        // Pour les fichiers, prendre le dossier parent
        if (File.Exists(item.Path))
        {
            return Path.GetDirectoryName(item.Path);
        }

        // Pour les dossiers, utiliser directement
        if (Directory.Exists(item.Path))
        {
            return item.Path;
        }

        return Path.GetDirectoryName(item.Path);
    }

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

            // Utiliser la réflexion pour compatibilité 10.10/10.11
            var method = _library.GetType().GetMethods()
                .FirstOrDefault(m => m.Name == "GetItemList" && m.GetParameters().Length == 1);

            if (method != null)
            {
                var result = method.Invoke(_library, new object[] { query });
                if (result is IEnumerable<BaseItem> items)
                {
                    return items.ToList();
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "PoolService: Failed to get media items");
        }

        return new List<BaseItem>();
    }

    private HashSet<Guid> GetAllMediaItemIds()
    {
        return GetAllMediaItems().Select(i => i.Id).ToHashSet();
    }

    private static string FormatSize(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB", "TB" };
        double len = bytes;
        int order = 0;
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len /= 1024;
        }
        return $"{len:0.##} {sizes[order]}";
    }

    private static string GetContentType(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            _ => "application/octet-stream"
        };
    }

    #endregion

    // Classe interne pour désérialiser l'état de rotation
    private class RotationState
    {
        public Dictionary<string, long>? LastRotatedUtcByItem { get; set; }
    }
}
