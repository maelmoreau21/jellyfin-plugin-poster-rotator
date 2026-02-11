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
        ├── Api/
        │   └── PoolController.cs    # API REST pour la gestion des pools
        ├── Helpers/
        │   └── PluginHelpers.cs     # Utilitaires partagés (GuessExt, FormatSize, RotationState…)
        ├── Models/
        │   ├── PoolInfo.cs          # Modèle d'un pool et ses images
        │   └── PoolStatistics.cs    # Modèle de statistiques
        ├── Services/
        │   └── PoolService.cs       # Service métier pour les pools (~670 lignes)
        ├── Web/
        │   ├── config.html          # Interface de configuration
        │   └── pool_manager.html    # Interface Pool Manager (split-view)
        ├── Plugin.cs                # Enregistrement du plugin
        ├── Configuration.cs         # Classe de configuration
        ├── PosterRotatorService.cs  # Service principal de rotation (~1010 lignes)
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
- **Pages**: `config.html`, `pool_manager.html`

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
- Enregistre `PosterRotatorService` et `PoolService` en singletons
- Pas de `IProviderManager` injecté directement — utilise `IServiceProvider` pour résolution DI

### `PoolController.cs` (API REST)
| Endpoint | Méthode | Description |
|----------|---------|-------------|
| `/PosterRotator/Stats` | GET | Statistiques globales |
| `/PosterRotator/Items` | GET | Liste des items avec pools |
| `/PosterRotator/Pool/{id}` | GET | Détails d'un pool |
| `/PosterRotator/Pool/{id}` | POST | Upload image |
| `/PosterRotator/Pool/{id}/{file}` | DELETE | Supprimer image |
| `/PosterRotator/Search/{id}` | GET | Rechercher images providers |
| `/PosterRotator/Pool/{id}/AddFromUrl` | POST | Ajouter depuis URL |
| `/PosterRotator/Cleanup` | POST | Nettoyer orphelins |

### `PoolService.cs`
Méthodes principales:
- `GetStatisticsAsync()` - Stats globales
- `GetAllPoolsAsync()` - Liste tous les pools
- `GetPoolForItemAsync()` - Pool d'un item
- `AddImageToPoolAsync()` - Upload image
- `DeleteImageFromPoolAsync()` - Supprimer image
- `SearchRemoteImagesAsync()` - Recherche providers (via DI, sans réflexion)
- `AddImageFromUrlAsync()` - Télécharger depuis URL
- `CleanupOrphanedPoolsAsync()` - Nettoyage orphelins
- `ForceRotateAsync()` - Rotation forcée immédiate

### `PosterRotatorService.cs`
- `RunAsync()` - Point d'entrée de la rotation
- `ProcessItemAsync()` - Traite un item (pool top-up + rotation + notification Jellyfin)
- `TryTopUpFromProvidersAsync()` - Télécharge images manquantes via providers DI
- `GetOriginalLanguage()` - Détecte la langue originale (accès direct aux propriétés)
- `DetectLanguageFromTitle()` - Détection heuristique de langue (Unicode)
- `GetLibraryRootPaths()` - Appel direct `_library.GetVirtualFolders()`
- `NudgeLibraryRoot()` - Notification par touch fichier (sans réflexion)
- `ResolveImageProviders()` - Résolution DI via `IServiceProvider`

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
| `IHttpClientFactory` | Directe (DI) | Téléchargement images (pool top-up, URL import) |
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
6. **Helpers partagés**: `Helpers/PluginHelpers.cs` centralise le code commun (GuessExtFromUrl, FormatSize, RotationState, GetItemDirectory)
7. **Providers cachés**: Les providers sont résolus une seule fois par run via `_cachedProviders`
8. **Écriture atomique**: `pool_languages.json` et `rotation_state.json` écrits via .tmp + rename
9. **Logging optimisé**: Debug logging gardé avec `IsEnabled(LogLevel.Debug)`, résumé de fin de run

---

## ✅ Fonctionnalités Implémentées (v1.4.0)

- [x] Pool Manager avec interface split-view
- [x] Statistiques (pools, images, taille, orphelins)
- [x] Recherche et filtrage des pools
- [x] Visualisation des images du pool
- [x] Recherche d'images via providers Jellyfin (DI, sans réflexion)
- [x] Ajout d'images depuis URL
- [x] Import manuel (drag & drop)
- [x] Suppression d'images
- [x] Nettoyage des pools orphelins
- [x] Préférences de langue
- [x] Détection automatique langue originale (VO)
- [x] Force Rotate depuis Pool Manager
- [x] Indicateur de santé des pools (couleurs)
- [x] Badge "Active" sur l'image courante
- [x] **v1.4.0**: Suppression totale de System.Reflection
- [x] **v1.4.0 Phase 2**: Code dedup (`Helpers/PluginHelpers.cs`), bugs fixes (SafeOverwrite, SaveState, double touch, race condition), perf (providers cache, stats optimisation), logging amélioré
