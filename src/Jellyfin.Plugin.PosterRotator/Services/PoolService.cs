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
    /// Force la rotation immédiate d'un item vers la prochaine image du pool.
    /// Compatible avec le format rotation_state.json de PosterRotatorService.
    /// </summary>
    public Task<bool> ForceRotateAsync(Guid itemId, CancellationToken ct = default)
    {
        var item = _library.GetItemById(itemId);
        if (item == null)
        {
            _log.LogWarning("PoolService: Cannot force rotate - Item {ItemId} not found", itemId);
            return Task.FromResult(false);
        }

        var poolDir = GetPoolDirectory(item);
        if (string.IsNullOrEmpty(poolDir) || !Directory.Exists(poolDir))
        {
            _log.LogWarning("PoolService: Cannot force rotate - No pool for {Item}", item.Name);
            return Task.FromResult(false);
        }

        // Récupérer les images du pool (même ordre que PosterRotatorService.PickNextFor)
        var images = Directory.GetFiles(poolDir)
            .Where(f => ImageExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .OrderBy(f => Path.GetFileName(f).StartsWith("pool_currentprimary", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ThenBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (images.Count < 2)
        {
            _log.LogWarning("PoolService: Cannot force rotate - Not enough images for {Item}", item.Name);
            return Task.FromResult(false);
        }

        // Charger l'état de rotation existant (format compatible PosterRotatorService)
        var statePath = Path.Combine(poolDir, "rotation_state.json");
        var state = LoadRotationState(statePath);
        var key = item.Id.ToString();

        // Déterminer l'index actuel via l'état sauvegardé
        int lastIdx = state.LastIndexByItem.TryGetValue(key, out var v) ? v : 0;
        int nextIdx = lastIdx % images.Count;
        
        // Éviter pool_currentprimary si d'autres images existent
        var chosen = images[nextIdx];
        if (Path.GetFileName(chosen).StartsWith("pool_currentprimary", StringComparison.OrdinalIgnoreCase) && images.Count > 1)
        {
            nextIdx = (nextIdx + 1) % images.Count;
            chosen = images[nextIdx];
        }

        try
        {
            // Déterminer la destination (comme PosterRotatorService.ProcessItemAsync)
            var currentPrimary = item.GetImagePath(ImageType.Primary);
            string destinationPath;

            if (!string.IsNullOrEmpty(currentPrimary))
            {
                destinationPath = currentPrimary;
            }
            else
            {
                var itemDir = GetItemDirectory(item);
                if (string.IsNullOrEmpty(itemDir)) return Task.FromResult(false);
                destinationPath = Path.Combine(itemDir, "poster" + Path.GetExtension(chosen));
            }

            // SafeOverwrite: temp file + rename pour éviter les corruptions
            var dir = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var tmp = destinationPath + ".tmp";
            File.Copy(chosen, tmp, overwrite: true);
            File.Move(tmp, destinationPath, overwrite: true);

            // Mettre à jour l'état (format compatible PosterRotatorService)
            state.LastIndexByItem[key] = nextIdx + 1;
            state.LastRotatedUtcByItem[key] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            SaveRotationState(statePath, state);

            // Notifier Jellyfin du changement
            try
            {
                item.SetImagePath(ImageType.Primary, destinationPath);
                item.UpdateToRepositoryAsync(ItemUpdateType.ImageUpdate, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "PoolService: Could not update repository for {Item}", item.Name);
            }

            _log.LogInformation("PoolService: Forced rotation for {Item} to {Image}", item.Name, Path.GetFileName(chosen));
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "PoolService: Failed to force rotate {Item}", item.Name);
            return Task.FromResult(false);
        }
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

    /// <summary>
    /// Recherche des images disponibles via les providers Jellyfin.
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
            // Utiliser l'API native de Jellyfin pour rechercher des images
            // On accède à IProviderManager via le service provider
            var providerManager = GetProviderManager();
            if (providerManager == null)
            {
                _log.LogWarning("PoolService: Cannot access IProviderManager");
                return results;
            }

            // Appeler GetRemoteImages via réflexion pour compatibilité
            var method = providerManager.GetType().GetMethods()
                .FirstOrDefault(m => m.Name == "GetRemoteImages" && 
                                    m.GetParameters().Length >= 2);

            if (method != null)
            {
                // Essayer différentes signatures
                object? imageResult = null;
                
                try
                {
                    // Jellyfin 10.10+: GetRemoteImages(item, options, ct)
                    var optionsType = Type.GetType("MediaBrowser.Controller.Providers.RemoteImageQuery, Jellyfin.Controller") ??
                                     Type.GetType("MediaBrowser.Model.Providers.RemoteImageQuery, Jellyfin.Model");
                    
                    if (optionsType != null)
                    {
                        var options = Activator.CreateInstance(optionsType);
                        optionsType.GetProperty("ImageType")?.SetValue(options, ImageType.Primary);
                        optionsType.GetProperty("IncludeAllLanguages")?.SetValue(options, true);
                        
                        var task = method.Invoke(providerManager, new[] { item, options, ct }) as Task;
                        if (task != null)
                        {
                            await task.ConfigureAwait(false);
                            imageResult = task.GetType().GetProperty("Result")?.GetValue(task);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _log.LogDebug(ex, "PoolService: Failed to get remote images via new API");
                }

                // Parser les résultats
                if (imageResult != null)
                {
                    var imagesProperty = imageResult.GetType().GetProperty("Images");
                    if (imagesProperty?.GetValue(imageResult) is System.Collections.IEnumerable images)
                    {
                        foreach (var img in images)
                        {
                            var url = img.GetType().GetProperty("Url")?.GetValue(img) as string;
                            var provider = img.GetType().GetProperty("ProviderName")?.GetValue(img) as string;
                            var lang = img.GetType().GetProperty("Language")?.GetValue(img) as string;
                            var width = img.GetType().GetProperty("Width")?.GetValue(img) as int?;
                            var height = img.GetType().GetProperty("Height")?.GetValue(img) as int?;
                            var thumb = img.GetType().GetProperty("ThumbnailUrl")?.GetValue(img) as string;

                            if (!string.IsNullOrEmpty(url))
                            {
                                results.Add(new Api.RemoteImageResult
                                {
                                    Url = url,
                                    ProviderName = provider ?? "Unknown",
                                    Language = lang,
                                    Width = width,
                                    Height = height,
                                    ThumbnailUrl = thumb ?? url
                                });
                            }
                        }
                    }
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
            using var http = new System.Net.Http.HttpClient();
            using var response = await http.GetAsync(url, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            // Deviner l'extension depuis l'URL ou le content-type
            var ext = GuessExtensionFromUrl(url) ?? ".jpg";
            var fileName = $"pool_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}{ext}";
            var destPath = Path.Combine(poolDir, fileName);

            using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var fs = new FileStream(destPath, FileMode.Create, FileAccess.Write);
            await stream.CopyToAsync(fs, ct).ConfigureAwait(false);

            _log.LogInformation("PoolService: Downloaded image from URL to {Path}", destPath);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "PoolService: Failed to download image from URL for {Item}", item.Name);
            return false;
        }
    }

    private object? GetProviderManager()
    {
        try
        {
            // Le provider manager est injecté via DI dans Jellyfin
            // On peut y accéder via le service provider global
            var assembly = typeof(ILibraryManager).Assembly;
            var providerManagerType = assembly.GetType("MediaBrowser.Controller.Providers.IProviderManager");
            
            // Essayer différentes méthodes pour obtenir le provider manager
            var libraryType = _library.GetType();
            
            // Chercher une propriété ou un champ qui pourrait contenir le provider manager
            foreach (var prop in libraryType.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance))
            {
                if (prop.PropertyType.Name.Contains("ProviderManager"))
                {
                    return prop.GetValue(_library);
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "PoolService: Could not get provider manager");
        }
        return null;
    }

    private static string? GuessExtensionFromUrl(string url)
    {
        try
        {
            var uri = new Uri(url);
            var path = uri.AbsolutePath.ToLowerInvariant();
            
            if (path.EndsWith(".jpg") || path.EndsWith(".jpeg")) return ".jpg";
            if (path.EndsWith(".png")) return ".png";
            if (path.EndsWith(".webp")) return ".webp";
            if (path.EndsWith(".gif")) return ".gif";
        }
        catch { }
        
        return ".jpg";
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

        // Déterminer l'image actuelle en comparant la taille du poster primaire
        // avec les images du pool (les noms de fichiers ne correspondent jamais)
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

            // Comparer par taille de fichier : si le poster actuel a la même taille
            // qu'une image du pool, c'est probablement la même image
            var isCurrent = currentPrimarySize > 0 && fi.Length == currentPrimarySize;

            images.Add(new PoolImage
            {
                FileName = fileName,
                Url = $"/PosterRotator/Pool/{item.Id}/Image/{fileName}",
                SizeBytes = fi.Length,
                SizeFormatted = FormatSize(fi.Length),
                CreatedAt = fi.CreationTimeUtc,
                IsCurrent = isCurrent,
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
        var rotState = LoadRotationState(statePath);
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

    // Chargement/sauvegarde de l'état de rotation (format PosterRotatorService)
    private static InternalRotationState LoadRotationState(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<InternalRotationState>(json) ?? new InternalRotationState();
            }
        }
        catch { /* ignore corrupt state */ }

        return new InternalRotationState();
    }

    private static void SaveRotationState(string path, InternalRotationState state)
    {
        try
        {
            var json = JsonSerializer.Serialize(state);
            File.WriteAllText(path, json);
        }
        catch { /* ignore */ }
    }

    #endregion

    /// <summary>
    /// Format compatible avec PosterRotatorService.RotationState.
    /// </summary>
    private sealed class InternalRotationState
    {
        public Dictionary<string, int> LastIndexByItem { get; set; } = new();
        public Dictionary<string, long> LastRotatedUtcByItem { get; set; } = new();
    }
}
