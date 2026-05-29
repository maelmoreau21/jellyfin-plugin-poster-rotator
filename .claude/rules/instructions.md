# Jellyfin Poster Rotator - Project Structure

> **Purpose**: This document serves as a memory aid for the AI to avoid any hallucinations and maintain a consistent understanding of the project.

---

## 📁 File Structure

```
jellyfin-plugin-poster-rotator-1/
├── .agent/
│   └── PROJECT_STRUCTURE.md         # This file (AI memory)
├── manifest.json                    # Plugin manifest for the Jellyfin repository
├── README.md                        # Main documentation
├── jellyfin-plugin-poster-rotator.sln
└── src/
    └── Jellyfin.Plugin.PosterRotator/
        ├── Api/
        │   └── PurgeController.cs   # REST API: POST PurgeAllPools (pool deletion)
        ├── Helpers/
        │   ├── PluginHelpers.cs     # Utilities (GuessExt, FormatSize, GetImageDimensions, RetryAsync…)
        │   └── ImageHash.cs         # Perceptual hash (aHash, Hamming distance, pool_hashes.json)
        ├── Web/
        │   └── config.html          # Configuration interface
        ├── Plugin.cs                # Plugin registration
        ├── Configuration.cs         # Configuration class
        ├── PosterRotatorService.cs  # Main rotation service (~1060 lines)
        ├── PosterRotationTask.cs    # Jellyfin scheduled task
        ├── ServiceRegistrator.cs    # Dependency injection
        └── Jellyfin.Plugin.PosterRotator.csproj
```

---

## 🔧 Key Files

### `Jellyfin.Plugin.PosterRotator.csproj`
- **Target Framework**: `net9.0`
- **Version**: `1.5.6.0`
- **Packages**: Jellyfin.Model, Controller, Common, Extensions `10.11.6`

### `Plugin.cs`
- **Class**: `Plugin : BasePlugin<Configuration>, IHasWebPages`
- **GUID**: `7f6eea8b-0e9c-4cbd-9d2a-31f9a37ce2b7`
- **Pages**: `config.html`

### `Configuration.cs`
Main properties:
- `PoolSize` (default: 5)
- `SequentialRotation`
- `LockImagesAfterFill`
- `MinHoursBetweenSwitches` (default: 23)
- `EnableSeasonPosters`, `EnableEpisodePosters`
- `AutoCleanupOrphanedPools`, `CleanupIntervalDays`
- `EnableLanguageFilter`, `PreferredLanguage`, `MaxPreferredLanguageImages`
- `UseOriginalLanguageAsFallback`, `FallbackLanguage`, `IncludeUnknownLanguage`
- **v1.5.0**: `MinImageWidth` (default: 500), `MinImageHeight` (default: 750)
- **v1.5.6**: `EnableDuplicateDetection` (default: true) — visual duplicate detection upon download

### `ServiceRegistrator.cs`
- Registers `PosterRotatorService` as a singleton

### `PosterRotatorService.cs`
- `RunAsync()` - Rotation entry point
- `ProcessItemAsync()` - Processes an item (pool top-up + rotation + notification)
- `TryTopUpFromProvidersAsync()` - Downloads images via `IProviderManager` (GetImageProviders with null options)
  - **v1.5.0**: `RetryAsync` on `GetImages()` and `GetAsync()` (exponential backoff 1s→2s→4s)
  - **v1.5.0**: Quality filter (pre-download via RemoteImageInfo, post-download via header parsing)
  - **v1.5.6**: URL dedup (`pool_urls.json`) - Blocks download if URL has already been seen
  - **v1.5.0**: Perceptual dedup (aHash + Hamming distance, reject if ≤10 bits of difference)
- `GetOriginalLanguage()` - Detects original language
- `GetLibraryRootPaths()` - Direct call `_library.GetVirtualFolders()`
- `NudgeLibraryRoot()` - Notification by file touch
- **v1.5.0**: `PurgeAllPools()` - Deletes all `.poster_pool` files across all libraries

### `PluginHelpers.cs`
- `GuessExtFromUrl()` / `GuessExtFromMime()` - Extensions from URL/mime
- `FormatSize()` - File size formatting
- `GetContentType()` - Detects mime type
- `GetItemDirectory()` - Path to an item's directory
- `LoadRotationState()` / `SaveRotationState()` - Atomic write (tmp + rename)
- `UpdateJsonMapFile()` / `CountInJsonMap()` - Atomic JSON map
- **v1.5.0**: `GetImageDimensions()` - Dimensions via JPEG/PNG/WebP/GIF headers (no decoding)
- **v1.5.0**: `RetryAsync()` - Generic retry with exponential backoff

### `ImageHash.cs` (v1.5.0)
- `ComputeHash()` → `ulong` - Perceptual hash by bytes sampling (64-bit)
- `HammingDistance()` - Hamming distance between 2 hashes
- `IsDuplicate()` - Duplicate detection (threshold: 10 bits)
- `LoadHashes()` / `SaveHash()` / `RemoveHash()` - Atomic JSON persistence

### `PurgeController.cs` (v1.5.0)
- `POST /PosterRotator/PurgeAllPools` - Deletes all pools, returns `{ DeletedCount: N }`
- Authorized for admins only (`Policies.RequiresElevation`)

---

## 📂 Pool Structure

```
/path/to/movie/
├── movie.mkv
├── poster.jpg
└── .poster_pool/
    ├── pool_original.jpg            # Initial poster backup (formerly pool_currentprimary)
    ├── pool_1705123456789.jpg       # Downloaded posters
    ├── rotation_state.json          # Rotation state
    ├── pool_languages.json          # Language metadata
    ├── pool_urls.json               # History of downloaded URLs (v1.5.6)
    ├── pool_hashes.json             # Perceptual hashes (v1.5.0)
    ├── pool_order.json              # Custom order
    └── pool.lock                    # Lock file
```

---

## 🔌 Jellyfin APIs Used

| Service | Injection | Usage |
|---------|-----------|-------------|
| `ILibraryManager` | Direct (DI) | `GetItemList()`, `GetVirtualFolders()`, `GetItemById()` |
| `IServiceProvider` | Direct (DI) | Resolving `IEnumerable<IRemoteImageProvider>` |
| `IHttpClientFactory` | Direct (DI) | Image downloading (pool top-up) |

---

## ✅ Features (v1.5.0)

- [x] Automatic poster rotation (sequential or random)
- [x] Local pool per item (.poster_pool)
- [x] Automatic top-up via Jellyfin providers
- [x] Language preferences (filtering, preferred language, auto VO)
- [x] Automatic original language detection
- [x] Automatic cleanup of orphaned pools
- [x] Pool locking after fill
- [x] Support for Movies, Shows, Seasons, Episodes
- [x] Jellyfin configuration page
- [x] **v1.5.0**: Image quality filter (minimum dimensions)
- [x] **v1.5.0**: Retry with exponential backoff (providers + downloads)
- [x] **v1.5.6**: URL duplicate detection (pool_urls.json)
- [x] **v1.5.0**: Visual duplicate detection (perceptual aHash)
- [x] **v1.5.0**: Purge all pools button (API + UI)
