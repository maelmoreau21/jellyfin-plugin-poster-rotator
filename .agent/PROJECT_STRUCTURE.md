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
        │   └── PurgeController.cs   # API REST: POST PurgeAllPools (suppression pools)
        ├── Helpers/
        │   ├── PluginHelpers.cs     # Utilitaires (GuessExt, FormatSize, GetImageDimensions, RetryAsync…)
        │   └── ImageHash.cs         # Hash perceptuel (aHash, Hamming distance, pool_hashes.json)
        ├── Web/
        │   └── config.html          # Interface de configuration
        ├── Plugin.cs                # Enregistrement du plugin
        ├── Configuration.cs         # Classe de configuration
        ├── PosterRotatorService.cs  # Service principal de rotation (~1060 lignes)
        ├── PosterRotationTask.cs    # Tâche planifiée Jellyfin
        ├── ServiceRegistrator.cs    # Injection de dépendances
        └── Jellyfin.Plugin.PosterRotator.csproj
```

---

## 🔧 Fichiers Clés

### `Jellyfin.Plugin.PosterRotator.csproj`
- **Target Framework**: `net9.0`
- **Version**: `1.5.0.0`
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
- **v1.5.0**: `MinImageWidth` (défaut: 500), `MinImageHeight` (défaut: 750)
- **v1.5.0**: `EnableDuplicateDetection` (défaut: false) — détection doublons visuels au téléchargement

### `ServiceRegistrator.cs`
- Enregistre `PosterRotatorService` en singleton

### `PosterRotatorService.cs`
- `RunAsync()` - Point d'entrée de la rotation
- `ProcessItemAsync()` - Traite un item (pool top-up + rotation + notification)
- `TryTopUpFromProvidersAsync()` - Télécharges images via providers DI (parallel, SemaphoreSlim(3))
  - **v1.5.0**: `RetryAsync` sur `GetImages()` et `GetAsync()` (backoff exponentiel 1s→2s→4s)
  - **v1.5.0**: Filtre qualité (pre-download via RemoteImageInfo, post-download via header parsing)
  - **v1.5.0**: Dedup perceptuel (aHash + Hamming distance, rejet si ≤10 bits de différence)
- `GetOriginalLanguage()` - Détecte la langue originale
- `GetLibraryRootPaths()` - Appel direct `_library.GetVirtualFolders()`
- `NudgeLibraryRoot()` - Notification par touch fichier
- `ResolveImageProviders()` - Résolution DI via `IServiceProvider`
- **v1.5.0**: `PurgeAllPools()` - Supprime tous les `.poster_pool` de toutes les bibliothèques

### `PluginHelpers.cs`
- `GuessExtFromUrl()` / `GuessExtFromMime()` - Extensions depuis URL/mime
- `FormatSize()` - Formatage taille fichier
- `GetContentType()` - Détecte le mime type
- `GetItemDirectory()` - Chemin dossier d'un item
- `LoadRotationState()` / `SaveRotationState()` - Écriture atomique (tmp + rename)
- `UpdateJsonMapFile()` / `CountInJsonMap()` - JSON map atomique
- **v1.5.0**: `GetImageDimensions()` - Dimensions via headers JPEG/PNG/WebP/GIF (pas de décodage)
- **v1.5.0**: `RetryAsync()` - Retry générique avec backoff exponentiel

### `ImageHash.cs` (v1.5.0)
- `ComputeHash()` → `ulong` - Hash perceptuel par échantillonnage bytes (64-bit)
- `HammingDistance()` - Distance de Hamming entre 2 hashes
- `IsDuplicate()` - Détection doublon (seuil: 10 bits)
- `LoadHashes()` / `SaveHash()` / `RemoveHash()` - Persistence JSON atomique

### `PurgeController.cs` (v1.5.0)
- `POST /PosterRotator/PurgeAllPools` - Supprime tous les pools, renvoie `{ DeletedCount: N }`
- Autorisé admin uniquement (`Policies.RequiresElevation`)

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
    ├── pool_hashes.json             # Hashes perceptuels (v1.5.0)
    ├── pool_order.json              # Ordre personnalisé
    └── pool.lock                    # Verrouillage
```

---

## 🔌 APIs Jellyfin Utilisées

| Service | Injection | Utilisation |
|---------|-----------|-------------|
| `ILibraryManager` | Directe (DI) | `GetItemList()`, `GetVirtualFolders()`, `GetItemById()` |
| `IServiceProvider` | Directe (DI) | Résolution `IEnumerable<IRemoteImageProvider>` |
| `IHttpClientFactory` | Directe (DI) | Téléchargement images (pool top-up) |

---

## ✅ Fonctionnalités (v1.5.0)

- [x] Rotation automatique de posters (séquentielle ou aléatoire)
- [x] Pool local par item (.poster_pool)
- [x] Top-up automatique via providers Jellyfin
- [x] Préférences de langue (filtrage, langue préférée, VO auto)
- [x] Détection automatique langue originale
- [x] Nettoyage automatique des pools orphelins
- [x] Verrouillage des pools après remplissage
- [x] Support Films, Séries, Saisons, Épisodes
- [x] Page de configuration Jellyfin
- [x] **v1.5.0**: Filtre qualité d'image (dimensions minimales)
- [x] **v1.5.0**: Retry avec backoff exponentiel (providers + downloads)
- [x] **v1.5.0**: Détection doublons visuels (hash perceptuel aHash)
- [x] **v1.5.0**: Bouton purge tous les pools (API + UI)
