# Jellyfin Poster Rotator - Project Structure

> **Purpose**: Ce document sert de mémoire pour l'IA afin d'éviter toute hallucination et de maintenir une compréhension cohérente du projet.

---

## 📁 Structure des Fichiers

```
jellyfin-plugin-poster-rotator-1/
├── .agent/
│   └── PROJECT_STRUCTURE.md         # Ce fichier (mémoire IA)
├── manifest.json                    # Manifest du plugin pour le repository Jellyfin
├── README.md                        # Documentation principale
├── jellyfin-plugin-poster-rotator.sln
└── src/
    └── Jellyfin.Plugin.PosterRotator/
        ├── Helpers/
        │   └── PluginHelpers.cs     # Utilitaires partagés (GuessExt, FormatSize, RotationState…)
        ├── Web/
        │   └── config.html          # Interface de configuration
        ├── Plugin.cs                # Enregistrement du plugin
        ├── Configuration.cs         # Classe de configuration
        ├── PosterRotatorService.cs  # Service principal de rotation (~990 lignes)
        ├── PosterRotationTask.cs    # Tâche planifiée Jellyfin
        ├── ServiceRegistrator.cs    # Injection de dépendances
        └── Jellyfin.Plugin.PosterRotator.csproj
```

---

## 🔧 Fichiers Clés

### `Jellyfin.Plugin.PosterRotator.csproj`
- **Target Framework**: `net9.0`
- **Version**: `1.4.0.0`
- **Packages**: Jellyfin.Model, Controller, Common, Extensions `10.11.6`

### `Plugin.cs`
- **Classe**: `Plugin : BasePlugin<Configuration>, IHasWebPages`
- **GUID**: `7f6eea8b-0e9c-4cbd-9d2a-31f9a37ce2b7`
- **Pages**: `config.html`

### `Configuration.cs`
Propriétés principales:
- `PoolSize` (défaut: 5)
- `SequentialRotation`
- `LockImagesAfterFill`
- `MinHoursBetweenSwitches` (défaut: 23)
- `EnableSeasonPosters`, `EnableEpisodePosters`
- `AutoCleanupOrphanedPools`, `CleanupIntervalDays`
- `EnableLanguageFilter`, `PreferredLanguage`, `MaxPreferredLanguageImages`
- `UseOriginalLanguageAsFallback`, `FallbackLanguage`, `IncludeUnknownLanguage`

### `ServiceRegistrator.cs`
- Enregistre `PosterRotatorService` en singleton
- Pas de `IProviderManager` injecté directement — utilise `IServiceProvider` pour résolution DI

### `PosterRotatorService.cs`
- `RunAsync()` - Point d'entrée de la rotation (summary de fin avec compteurs)
- `ProcessItemAsync()` - Traite un item (pool top-up + rotation + notification Jellyfin)
- `TryTopUpFromProvidersAsync()` - Télécharge images manquantes via providers DI (parallel, SemaphoreSlim(3))
- `GetOriginalLanguage()` - Détecte la langue originale (accès direct aux propriétés)
- `DetectLanguageFromTitle()` - Détection heuristique de langue (Unicode)
- `GetLibraryRootPaths()` - Appel direct `_library.GetVirtualFolders()`
- `NudgeLibraryRoot()` - Notification par touch fichier (sans réflexion)
- `ResolveImageProviders()` - Résolution DI via `IServiceProvider` (thread-safe, cachée par run)

### `PluginHelpers.cs`
- `GuessExtFromUrl()` - Détecte l'extension depuis URL/content-type
- `FormatSize()` - Formatage taille fichier
- `GetContentType()` - Détecte le mime type
- `GetItemDirectory()` - Chemin dossier d'un item
- `BuildMediaItemQuery()` - Requête centralisée pour les items média
- `LoadRotationState()` / `SaveRotationState()` - Écriture atomique (tmp + rename)
- `UpdateJsonMapFile()` - Écriture atomique pour pool_languages.json

---

## 📂 Structure des Pools

```
/path/to/movie/
├── movie.mkv
├── poster.jpg
└── .poster_pool/
    ├── pool_currentprimary.jpg      # Backup affiche initiale
    ├── pool_1705123456789.jpg       # Affiches téléchargées
    ├── rotation_state.json          # État rotation
    ├── pool_languages.json          # Métadonnées langue
    ├── pool_order.json              # Ordre personnalisé
    └── pool.lock                    # Verrouillage
```

---

## 🌍 Détection Langue Originale

La fonction `GetOriginalLanguage()` utilise plusieurs heuristiques (accès direct, sans réflexion):
1. Comparaison `item.OriginalTitle` vs `item.Name`
2. Détection caractères Unicode (japonais, coréen, chinois, russe, arabe)
3. Provider IDs (`item.ProviderIds` — AniDB → japonais)
4. Patterns dans le chemin (/anime/, /korean/)
5. Fallback configurable

---

## 🔌 APIs Jellyfin Utilisées

| Service | Injection | Utilisation |
|---------|-----------|-------------|
| `ILibraryManager` | Directe (DI) | `GetItemList()`, `GetVirtualFolders()`, `GetItemById()` |
| `IServiceProvider` | Directe (DI) | Résolution `IEnumerable<IRemoteImageProvider>` |
| `IHttpClientFactory` | Directe (DI) | Téléchargement images (pool top-up) |
| `IRemoteImageProvider` | Via IServiceProvider | `GetImages()`, `Supports()`, `GetSupportedImages()` |
| `BaseItem` | Via ILibraryManager | `UpdateToRepositoryAsync()`, `GetImagePath()`, `SetImagePath()` |
| `ImageType` | Enum | Types d'images (Primary, etc.) |

> **Important**: Aucune utilisation de `System.Reflection` — tous les appels sont directs et typés.

---

## ⚡ Points d'Attention

1. **Packages 10.11.6**: Nécessite .NET 9 SDK pour compiler
2. **Zéro réflexion**: Toute la réflexion a été supprimée en v1.4.0
3. **IHttpClientFactory**: Injection propre, pas de HttpClient statique
4. **Cooldown**: Respecte `MinHoursBetweenSwitches`
5. **Language Detection**: Heuristiques Unicode + métadonnées
6. **Helpers partagés**: `Helpers/PluginHelpers.cs` centralise le code commun
7. **Providers cachés**: Les providers sont résolus une seule fois par run via `_cachedProviders` (thread-safe avec `lock`)
8. **Écriture atomique**: `pool_languages.json` et `rotation_state.json` écrits via .tmp + rename
9. **Logging optimisé**: Debug logging gardé avec `IsEnabled(LogLevel.Debug)`, résumé de fin de run
10. **Top-up parallèle**: Téléchargements parallélisés via `SemaphoreSlim(3)`

---

## ✅ Fonctionnalités Implémentées (v1.4.0)

- [x] Rotation automatique de posters (séquentielle ou aléatoire)
- [x] Pool local par item (.poster_pool)
- [x] Top-up automatique via providers Jellyfin (DI, sans réflexion)
- [x] Préférences de langue (filtrage, langue préférée, VO auto)
- [x] Détection automatique langue originale (Unicode + heuristiques)
- [x] Nettoyage automatique des pools orphelins
- [x] Verrouillage des pools après remplissage
- [x] Support Films, Séries, Saisons, Épisodes
- [x] Page de configuration Jellyfin
- [x] **v1.4.0**: Suppression totale de System.Reflection
- [x] **v1.4.0 Phase 2**: Code dedup (`PluginHelpers.cs`), bugs fixes, perf (providers cache), logging amélioré
- [x] **v1.4.0 Phase 3**: Streaming images, path traversal fix, cache pools, top-up parallèle, thread-safety
