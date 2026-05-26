using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MediaBrowser.Controller.Configuration;

namespace Jellyfin.Plugin.PosterRotator;

public sealed class PosterRotatorLocalization : IPosterRotatorLocalization
{
    public const string AutoLanguage = "auto";
    public const string FallbackLanguage = "en";

    private static readonly Dictionary<string, string> LanguageNames = new(StringComparer.OrdinalIgnoreCase)
    {
        [AutoLanguage] = "Same as Jellyfin",
        ["en"] = "English",
        ["fr"] = "Francais"
    };

    private static readonly Dictionary<string, Dictionary<string, string>> Resources = new(StringComparer.OrdinalIgnoreCase)
    {
        ["en"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Language.Auto"] = "Same as Jellyfin",
            ["Language.English"] = "English",
            ["Language.French"] = "Francais",
            ["Tab.Pools"] = "Pools",
            ["Tab.Settings"] = "Settings",
            ["Stat.Pools"] = "Pools",
            ["Stat.DiskSpace"] = "Disk space",
            ["Stat.Orphans"] = "Orphans",
            ["Stat.Page"] = "Page",
            ["Heading.PluginDataPools"] = "PluginData pools",
            ["Heading.Settings"] = "Settings",
            ["Heading.Libraries"] = "Libraries",
            ["Heading.PosterLanguages"] = "Poster languages",
            ["Label.InterfaceLanguage"] = "Interface language",
            ["Label.Search"] = "Search",
            ["Label.Library"] = "Library",
            ["Label.Type"] = "Type",
            ["Label.State"] = "State",
            ["Label.PageSize"] = "Per page",
            ["Label.PoolStorage"] = "Pool storage",
            ["Label.MaxRotationsPerRun"] = "Maximum posters to change per run",
            ["Help.MaxRotationsPerRun"] = "(0 = no count limit. The internal delay between changes is still respected.)",
            ["Label.MaxImageSize"] = "Max image size (MB)",
            ["Label.MinWidth"] = "Minimum width",
            ["Label.MinHeight"] = "Minimum height",
            ["Label.ExtraPatterns"] = "Additional patterns",
            ["Label.PreferredLanguage"] = "Preferred language",
            ["Label.MaxPreferredLanguageImages"] = "Max preferred-language posters per pool",
            ["Label.FallbackLanguage"] = "Fallback language",
            ["Label.FallbackMode"] = "Fallback order",
            ["Option.AllLibraries"] = "All",
            ["Option.AllTypes"] = "All",
            ["Option.Movie"] = "Movies",
            ["Option.Series"] = "Series",
            ["Option.Season"] = "Seasons",
            ["Option.Episode"] = "Episodes",
            ["Option.BoxSet"] = "Collections",
            ["Option.AllStates"] = "All",
            ["Option.Errors"] = "With errors",
            ["Option.Empty"] = "Empty",
            ["Option.Filled"] = "With images",
            ["Option.MediaFolders"] = "Media folders",
            ["Option.OriginalThenConfigured"] = "Original language, then fallback",
            ["Option.ConfiguredThenOriginal"] = "Fallback, then original language",
            ["Option.OriginalOnly"] = "Original language only",
            ["Option.ConfiguredOnly"] = "Fallback only",
            ["Group.Navigation"] = "Navigation",
            ["Group.Refresh"] = "Refresh",
            ["Group.Rotation"] = "Rotation",
            ["Group.Purge"] = "Purge",
            ["Button.Previous"] = "Previous",
            ["Button.Next"] = "Next",
            ["Button.Refresh"] = "Refresh",
            ["Button.DownloadMissingPools"] = "Download missing pools",
            ["Button.RotateLibrary"] = "Rotate library",
            ["Button.PurgeAllPools"] = "Delete all pools",
            ["Button.PurgeLibrary"] = "Purge library",
            ["Button.PurgeItem"] = "Purge media",
            ["Button.RotateNow"] = "Rotate now",
            ["Button.Upload"] = "Import",
            ["Button.Open"] = "Open",
            ["Button.Delete"] = "Delete",
            ["Button.Save"] = "Save",
            ["Table.Media"] = "Media",
            ["Table.Type"] = "Type",
            ["Table.Library"] = "Library",
            ["Table.Images"] = "Images",
            ["Table.Size"] = "Size",
            ["Table.LastRotation"] = "Last rotation",
            ["Checkbox.SequentialRotation"] = "Use posters in order",
            ["Checkbox.LockImagesAfterFill"] = "Lock full pools",
            ["Checkbox.SeasonPosters"] = "Seasons",
            ["Checkbox.EpisodePosters"] = "Episodes",
            ["Checkbox.BlockPrivateUrls"] = "Block private URLs",
            ["Checkbox.DuplicateDetection"] = "Visual duplicates",
            ["Checkbox.FilterByLanguage"] = "Filter by language",
            ["Checkbox.IncludeUnknownLanguage"] = "Include images without language",
            ["Checkbox.AllowAnyLanguageFallback"] = "Allow any language as a last resort",
            ["Help.SequentialRotation"] = "Enabled: uses the next image from the pool at each rotation. Disabled: picks a random poster. Does not change the delay between rotations.",
            ["Message.NoLibraries"] = "No libraries detected.",
            ["Message.NoName"] = "(no name)",
            ["Message.Unknown"] = "Unknown",
            ["Message.Pool"] = "Pool",
            ["Message.LibrariesUnavailable"] = "Jellyfin libraries unavailable.",
            ["Message.DiagnosticsError"] = "Diagnostics error.",
            ["Message.Loading"] = "Loading...",
            ["Message.NoPools"] = "No pools found.",
            ["Message.PoolsStatus"] = "{0} pool(s), page {1} / {2}",
            ["Message.PoolMetaCurrent"] = "{0} image(s), {1}, rotation: {2}, current: {3} ({4})",
            ["Message.PoolMetaNoCurrent"] = "{0} image(s), {1}, rotation: {2}, current: not detected",
            ["Message.EmptyPool"] = "Empty pool.",
            ["Message.Current"] = "Current",
            ["Message.PreviewUnavailable"] = "Preview unavailable",
            ["Message.PoolsLoadError"] = "Pool loading error.",
            ["Message.PoolDetailLoadError"] = "Unable to load the pool.",
            ["Message.RotatePoolError"] = "Rotation is unavailable for this pool.",
            ["Message.SelectLibrary"] = "Choose a library in the filter.",
            ["Message.RotateLibraryFallback"] = "Rotation complete.",
            ["Message.RotateLibraryError"] = "Library rotation unavailable.",
            ["Message.OpenPoolBeforePurge"] = "Open a pool before purging a media item.",
            ["Message.ConfirmPurge"] = "Confirm {0} purge?",
            ["Message.PurgeResult"] = "{0} pool(s) deleted.",
            ["Message.PurgeError"] = "Purge unavailable.",
            ["Message.ChooseImage"] = "Choose an image.",
            ["Message.UploadRejected"] = "Import rejected.",
            ["Message.ConfirmImageDelete"] = "Delete this image from the pool?",
            ["Message.DeleteError"] = "Delete unavailable.",
            ["Message.DownloadStarting"] = "Downloading missing pools...",
            ["Message.DownloadProgressive"] = "Progressive download in progress...",
            ["Message.DownloadFallback"] = "Download complete.",
            ["Message.DownloadLimitedSuffix"] = " Download is progressive: run it again to continue.",
            ["Message.DownloadError"] = "Unable to download missing pools.",
            ["Message.DownloadShortError"] = "Download unavailable.",
            ["Message.ConfirmPurgeAll"] = "Delete all PluginData pools and legacy media-folder pools?",
            ["Message.PurgeAllStarting"] = "Deleting all pools...",
            ["Message.PurgeAllError"] = "Unable to delete all pools.",
            ["Message.SaveError"] = "Error while saving.",
            ["Message.LoadError"] = "Error while loading.",
            ["Task.RotatePools.Name"] = "Rotate pools",
            ["Task.RotatePools.Description"] = "Rotates eligible posters from existing local pools.",
            ["Task.DownloadMissingPools.Name"] = "Download missing pools",
            ["Task.DownloadMissingPools.Description"] = "Downloads images for missing or incomplete poster pools without rotating posters.",
            ["Task.CleanOrphanPools.Name"] = "Clean orphan pools",
            ["Task.CleanOrphanPools.Description"] = "Deletes PluginData pools whose Jellyfin media no longer exists.",
            ["Api.NoCandidateMedia"] = "No candidate media.",
            ["Api.DownloadResult"] = "{0} pool(s) completed, {1} image(s) added.",
            ["Api.DownloadLimitedSuffix"] = " Run limited by per-run budgets; run Download missing pools again to continue.",
            ["Api.MediaNotFound"] = "Media not found in Jellyfin.",
            ["Api.PoolNotFound"] = "Pool not found in the plugin folder.",
            ["Api.EmptyPool"] = "Empty pool.",
            ["Api.SaveImageRejected"] = "Jellyfin did not accept the selected image.",
            ["Api.RotationComplete"] = "Rotation complete.",
            ["Api.LibraryRotationResult"] = "{0}/{1} pool(s) rotated.",
            ["Api.NoFileReceived"] = "No file received.",
            ["Api.ImageTooLarge"] = "Image is too large.",
            ["Api.RotateEmptyPoolError"] = "Immediate rotation unavailable: empty pool.",
            ["Api.RotateSaveImageError"] = "Immediate rotation unavailable: SaveImage failed."
        },
        ["fr"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Language.Auto"] = "Meme langue que Jellyfin",
            ["Language.English"] = "English",
            ["Language.French"] = "Francais",
            ["Tab.Pools"] = "Pools",
            ["Tab.Settings"] = "Parametres",
            ["Stat.Pools"] = "Pools",
            ["Stat.DiskSpace"] = "Espace disque",
            ["Stat.Orphans"] = "Orphelins",
            ["Stat.Page"] = "Page",
            ["Heading.PluginDataPools"] = "Pools PluginData",
            ["Heading.Settings"] = "Parametres",
            ["Heading.Libraries"] = "Bibliotheques",
            ["Heading.PosterLanguages"] = "Langues des affiches",
            ["Label.InterfaceLanguage"] = "Langue de l'interface",
            ["Label.Search"] = "Recherche",
            ["Label.Library"] = "Bibliotheque",
            ["Label.Type"] = "Type",
            ["Label.State"] = "Etat",
            ["Label.PageSize"] = "Par page",
            ["Label.PoolStorage"] = "Stockage des pools",
            ["Label.MaxRotationsPerRun"] = "Nombre maximum d'affiches a changer par passage",
            ["Help.MaxRotationsPerRun"] = "(0 = aucune limite de nombre. Le delai interne entre deux changements reste respecte.)",
            ["Label.MaxImageSize"] = "Taille max image (MB)",
            ["Label.MinWidth"] = "Largeur minimale",
            ["Label.MinHeight"] = "Hauteur minimale",
            ["Label.ExtraPatterns"] = "Motifs supplementaires",
            ["Label.PreferredLanguage"] = "Langue preferee",
            ["Label.MaxPreferredLanguageImages"] = "Affiches max en langue preferee par pool",
            ["Label.FallbackLanguage"] = "Langue fallback",
            ["Label.FallbackMode"] = "Ordre de fallback",
            ["Option.AllLibraries"] = "Toutes",
            ["Option.AllTypes"] = "Tous",
            ["Option.Movie"] = "Films",
            ["Option.Series"] = "Series",
            ["Option.Season"] = "Saisons",
            ["Option.Episode"] = "Episodes",
            ["Option.BoxSet"] = "Collections",
            ["Option.AllStates"] = "Tous",
            ["Option.Errors"] = "Avec erreurs",
            ["Option.Empty"] = "Vides",
            ["Option.Filled"] = "Avec images",
            ["Option.MediaFolders"] = "Dossiers medias",
            ["Option.OriginalThenConfigured"] = "Langue originale puis fallback",
            ["Option.ConfiguredThenOriginal"] = "Fallback puis langue originale",
            ["Option.OriginalOnly"] = "Langue originale uniquement",
            ["Option.ConfiguredOnly"] = "Fallback uniquement",
            ["Group.Navigation"] = "Navigation",
            ["Group.Refresh"] = "Actualisation",
            ["Group.Rotation"] = "Rotation",
            ["Group.Purge"] = "Purge",
            ["Button.Previous"] = "Precedent",
            ["Button.Next"] = "Suivant",
            ["Button.Refresh"] = "Actualiser",
            ["Button.DownloadMissingPools"] = "Telecharger les pools manquants",
            ["Button.RotateLibrary"] = "Rotation bibliotheque",
            ["Button.PurgeAllPools"] = "Supprimer tous les pools",
            ["Button.PurgeLibrary"] = "Purger bibliotheque",
            ["Button.PurgeItem"] = "Purger media",
            ["Button.RotateNow"] = "Rotation maintenant",
            ["Button.Upload"] = "Importer",
            ["Button.Open"] = "Ouvrir",
            ["Button.Delete"] = "Supprimer",
            ["Button.Save"] = "Enregistrer",
            ["Table.Media"] = "Media",
            ["Table.Type"] = "Type",
            ["Table.Library"] = "Bibliotheque",
            ["Table.Images"] = "Images",
            ["Table.Size"] = "Taille",
            ["Table.LastRotation"] = "Derniere rotation",
            ["Checkbox.SequentialRotation"] = "Parcourir les affiches dans l'ordre",
            ["Checkbox.LockImagesAfterFill"] = "Verrouiller les pools pleins",
            ["Checkbox.SeasonPosters"] = "Saisons",
            ["Checkbox.EpisodePosters"] = "Episodes",
            ["Checkbox.BlockPrivateUrls"] = "Bloquer URLs privees",
            ["Checkbox.DuplicateDetection"] = "Doublons visuels",
            ["Checkbox.FilterByLanguage"] = "Filtrer par langue",
            ["Checkbox.IncludeUnknownLanguage"] = "Inclure les images sans langue",
            ["Checkbox.AllowAnyLanguageFallback"] = "Autoriser toutes les langues en dernier recours",
            ["Help.SequentialRotation"] = "Active: prend l'image suivante du pool a chaque rotation. Desactive: choisit une affiche au hasard. Ne change pas le delai entre deux rotations.",
            ["Message.NoLibraries"] = "Aucune bibliotheque detectee.",
            ["Message.NoName"] = "(sans nom)",
            ["Message.Unknown"] = "Inconnu",
            ["Message.Pool"] = "Pool",
            ["Message.LibrariesUnavailable"] = "Bibliotheques Jellyfin indisponibles.",
            ["Message.DiagnosticsError"] = "Erreur diagnostic.",
            ["Message.Loading"] = "Chargement...",
            ["Message.NoPools"] = "Aucun pool trouve.",
            ["Message.PoolsStatus"] = "{0} pool(s), page {1} / {2}",
            ["Message.PoolMetaCurrent"] = "{0} image(s), {1}, rotation: {2}, actuelle: {3} ({4})",
            ["Message.PoolMetaNoCurrent"] = "{0} image(s), {1}, rotation: {2}, actuelle: non detectee",
            ["Message.EmptyPool"] = "Pool vide.",
            ["Message.Current"] = "Actuelle",
            ["Message.PreviewUnavailable"] = "Apercu indisponible",
            ["Message.PoolsLoadError"] = "Erreur chargement pools.",
            ["Message.PoolDetailLoadError"] = "Impossible de charger le pool.",
            ["Message.RotatePoolError"] = "Rotation impossible pour ce pool.",
            ["Message.SelectLibrary"] = "Choisis une bibliotheque dans le filtre.",
            ["Message.RotateLibraryFallback"] = "Rotation terminee.",
            ["Message.RotateLibraryError"] = "Rotation bibliotheque impossible.",
            ["Message.OpenPoolBeforePurge"] = "Ouvre un pool avant de purger un media.",
            ["Message.ConfirmPurge"] = "Confirmer la purge {0} ?",
            ["Message.PurgeResult"] = "{0} pool(s) supprime(s).",
            ["Message.PurgeError"] = "Purge impossible.",
            ["Message.ChooseImage"] = "Choisis une image.",
            ["Message.UploadRejected"] = "Import refuse.",
            ["Message.ConfirmImageDelete"] = "Supprimer cette image du pool ?",
            ["Message.DeleteError"] = "Suppression impossible.",
            ["Message.DownloadStarting"] = "Telechargement des pools manquants...",
            ["Message.DownloadProgressive"] = "Telechargement progressif en cours...",
            ["Message.DownloadFallback"] = "Telechargement termine.",
            ["Message.DownloadLimitedSuffix"] = " Le telechargement est progressif: relancez pour continuer.",
            ["Message.DownloadError"] = "Telechargement des pools manquants impossible.",
            ["Message.DownloadShortError"] = "Telechargement impossible.",
            ["Message.ConfirmPurgeAll"] = "Supprimer tous les pools PluginData et les anciens pools dossiers medias ?",
            ["Message.PurgeAllStarting"] = "Suppression de tous les pools...",
            ["Message.PurgeAllError"] = "Suppression de tous les pools impossible.",
            ["Message.SaveError"] = "Erreur lors de la sauvegarde.",
            ["Message.LoadError"] = "Erreur lors du chargement.",
            ["Task.RotatePools.Name"] = "Rotation des pools",
            ["Task.RotatePools.Description"] = "Remplace les affiches eligibles depuis les pools locaux existants.",
            ["Task.DownloadMissingPools.Name"] = "Telechargement des pools manquants",
            ["Task.DownloadMissingPools.Description"] = "Telecharge les images des pools manquants ou incomplets sans changer les affiches.",
            ["Task.CleanOrphanPools.Name"] = "Nettoyage des pools orphelins",
            ["Task.CleanOrphanPools.Description"] = "Supprime les pools PluginData dont le media Jellyfin n'existe plus.",
            ["Api.NoCandidateMedia"] = "Aucun media candidat.",
            ["Api.DownloadResult"] = "{0} pool(s) completees, {1} image(s) ajoutees.",
            ["Api.DownloadLimitedSuffix"] = " Passage limite par les plafonds du run; relancez Telecharger les pools manquants pour continuer.",
            ["Api.MediaNotFound"] = "Media introuvable dans Jellyfin.",
            ["Api.PoolNotFound"] = "Pool introuvable dans le dossier plugin.",
            ["Api.EmptyPool"] = "Pool vide.",
            ["Api.SaveImageRejected"] = "Jellyfin n'a pas accepte l'image selectionnee.",
            ["Api.RotationComplete"] = "Rotation effectuee.",
            ["Api.LibraryRotationResult"] = "{0}/{1} pool(s) tournes.",
            ["Api.NoFileReceived"] = "Aucun fichier recu.",
            ["Api.ImageTooLarge"] = "Image trop volumineuse.",
            ["Api.RotateEmptyPoolError"] = "Rotation immediate impossible: pool vide.",
            ["Api.RotateSaveImageError"] = "Rotation immediate impossible: SaveImage a echoue."
        }
    };

    private readonly Func<string?> _serverUiCultureProvider;

    public PosterRotatorLocalization(IServerConfigurationManager serverConfigurationManager)
        : this(() => serverConfigurationManager.Configuration.UICulture)
    {
    }

    internal PosterRotatorLocalization(Func<string?> serverUiCultureProvider)
    {
        _serverUiCultureProvider = serverUiCultureProvider;
    }

    public string ResolveLanguage(string? configuredLanguage = null)
    {
        var normalized = NormalizeLanguageCode(configuredLanguage);
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Equals(AutoLanguage, StringComparison.OrdinalIgnoreCase))
        {
            normalized = NormalizeLanguageCode(_serverUiCultureProvider());
        }

        return IsSupportedLanguage(normalized) ? normalized! : FallbackLanguage;
    }

    public string Translate(string key, string? language = null)
    {
        var resolved = ResolveLanguage(language ?? Plugin.Instance?.Configuration?.InterfaceLanguage);
        if (Resources.TryGetValue(resolved, out var resource) && resource.TryGetValue(key, out var value))
            return value;

        if (Resources[FallbackLanguage].TryGetValue(key, out var fallback))
            return fallback;

        return key;
    }

    public PosterRotatorLocalizationResponse GetResponse(string? language = null)
    {
        var resolved = ResolveLanguage(language ?? Plugin.Instance?.Configuration?.InterfaceLanguage);
        var strings = new Dictionary<string, string>(Resources[FallbackLanguage], StringComparer.OrdinalIgnoreCase);
        if (Resources.TryGetValue(resolved, out var resource))
        {
            foreach (var pair in resource)
                strings[pair.Key] = pair.Value;
        }

        return new PosterRotatorLocalizationResponse
        {
            Language = resolved,
            Languages = GetLanguageOptions(resolved).ToList(),
            Strings = strings
        };
    }

    internal static string? NormalizeLanguageCode(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
            return null;

        var normalized = language.Trim().Replace('_', '-').ToLowerInvariant();
        if (normalized.Equals(AutoLanguage, StringComparison.OrdinalIgnoreCase))
            return AutoLanguage;

        var index = normalized.IndexOf('-', StringComparison.Ordinal);
        if (index > 0)
            normalized = normalized[..index];

        return normalized;
    }

    internal static bool IsSupportedLanguage(string? language) =>
        !string.IsNullOrWhiteSpace(language)
        && Resources.ContainsKey(language);

    private static IEnumerable<PosterRotatorLanguageOption> GetLanguageOptions(string resolvedLanguage)
    {
        var translated = Resources.TryGetValue(resolvedLanguage, out var resource) ? resource : Resources[FallbackLanguage];
        yield return new PosterRotatorLanguageOption
        {
            Value = AutoLanguage,
            Name = translated.TryGetValue("Language.Auto", out var auto) ? auto : LanguageNames[AutoLanguage]
        };
        yield return new PosterRotatorLanguageOption
        {
            Value = "en",
            Name = translated.TryGetValue("Language.English", out var en) ? en : LanguageNames["en"]
        };
        yield return new PosterRotatorLanguageOption
        {
            Value = "fr",
            Name = translated.TryGetValue("Language.French", out var fr) ? fr : LanguageNames["fr"]
        };
    }

    internal static string Format(string format, params object[] args) =>
        string.Format(CultureInfo.InvariantCulture, format, args);
}
