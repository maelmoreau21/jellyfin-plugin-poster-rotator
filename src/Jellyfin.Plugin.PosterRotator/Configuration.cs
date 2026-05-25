using MediaBrowser.Model.Plugins;
using System.Collections.Generic;

namespace Jellyfin.Plugin.PosterRotator;

public class LibraryRule
{
    public string Name { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
}

public enum PoolStorageMode
{
    PluginData = 0,
    MediaFolders = 1
}

public enum RotationCadenceProfile
{
    Balanced = 0,
    Conservative = 1,
    Visible = 2
}

public class Configuration : BasePluginConfiguration
{
    // Backwards-compatible simple list of library names (older versions)
    public List<string> Libraries { get; set; } = new();

    // Preferred shape for the UI: list of named rules with Enabled flag
    public List<LibraryRule> LibraryRules { get; set; } = new();

    public int PoolSize { get; set; } = 4;
    public bool SequentialRotation { get; set; } = false;
    public bool LockImagesAfterFill { get; set; } = false;
    public PoolStorageMode PoolStorageMode { get; set; } = PoolStorageMode.PluginData;
    public List<string> ExtraPosterPatterns { get; set; } = new();
    public int MinHoursBetweenSwitches { get; set; } = 72;
    public int MaxRotationsPerRun { get; set; } = 500;
    public int MaxDownloadsPerRun { get; set; } = 250;
    public int MaxProviderLookupsPerRun { get; set; } = 250;
    public int ProcessingBatchSize { get; set; } = 250;
    public RotationCadenceProfile CadenceProfile { get; set; } = RotationCadenceProfile.Balanced;
    public bool EnableSeasonPosters { get; set; } = false;
    public bool EnableEpisodePosters { get; set; } = false;
    // Legacy compatibility setting. The current rotation path uses Jellyfin SaveImage instead of a full scan.
    public bool TriggerLibraryScanAfterRotation { get; set; } = false;
    // Optional list of library root paths to process. When filled, overrides auto detection.
    public List<string> ManualLibraryRoots { get; set; } = new();

    // === Pool Management Options ===
    
    /// <summary>
    /// Si active, les pools orphelins (medias supprimes) seront automatiquement nettoyes.
    /// </summary>
    public bool AutoCleanupOrphanedPools { get; set; } = false;

    /// <summary>
    /// Intervalle en jours entre les nettoyages automatiques.
    /// </summary>
    public int CleanupIntervalDays { get; set; } = 7;

    // === Language Preferences ===

    /// <summary>
    /// Activer le filtrage par langue des images.
    /// </summary>
    public bool EnableLanguageFilter { get; set; } = false;

    /// <summary>
    /// Langue preferee pour les affiches (code ISO: fr, en, de, etc.).
    /// </summary>
    public string PreferredLanguage { get; set; } = "fr";

    /// <summary>
    /// Nombre maximum d'images dans la langue preferee.
    /// </summary>
    public int MaxPreferredLanguageImages { get; set; } = 2;

    /// <summary>
    /// Langue de fallback pour les autres images (code ISO ou vide pour toutes).
    /// Ignore si UseOriginalLanguageAsFallback est active.
    /// </summary>
    public string FallbackLanguage { get; set; } = "en";

    /// <summary>
    /// Utiliser automatiquement la langue originale du media (VO) comme fallback.
    /// Si active, FallbackLanguage est ignore et la vraie langue originale est detectee.
    /// </summary>
    public bool UseOriginalLanguageAsFallback { get; set; } = true;

    /// <summary>
    /// Inclure les images sans information de langue.
    /// </summary>
    public bool IncludeUnknownLanguage { get; set; } = true;

    // === Image Quality ===

    /// <summary>
    /// Largeur minimale des images telechargees (en pixels). Images plus petites sont rejetees.
    /// </summary>
    public int MinImageWidth { get; set; } = 500;

    /// <summary>
    /// Hauteur minimale des images telechargees (en pixels). Images plus petites sont rejetees.
    /// </summary>
    public int MinImageHeight { get; set; } = 750;

    /// <summary>
    /// Maximum remote image download size in megabytes.
    /// </summary>
    public int MaxDownloadMegabytes { get; set; } = 25;

    /// <summary>
    /// Reject remote image URLs pointing to localhost, private, or link-local networks.
    /// </summary>
    public bool BlockPrivateNetworkImageUrls { get; set; } = true;

    /// <summary>
    /// Activer la detection de doublons visuels lors du telechargement.
    /// Utilise un hash perceptuel pour eviter les images quasi-identiques.
    /// </summary>
    public bool EnableDuplicateDetection { get; set; } = true;
}
