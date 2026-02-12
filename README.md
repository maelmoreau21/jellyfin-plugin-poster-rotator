# Jellyfin Poster Rotator

Rotate poster images in Jellyfin by building a local pool of artwork next to each media item, then cycling the primary image on a schedule. This fork tracks the upstream project at [jonah-jamesen/jellyfin-plugin-poster-rotator](https://github.com/jonah-jamesen/jellyfin-plugin-poster-rotator) and adds a handful of quality-of-life fixes and compatibility updates.

---

## Key Features

- **Automatic Rotation**: Cycles through poster images on a schedule
- **Provider Search**: Download images from TMDb, Fanart, etc.
- **Language Preferences**: Prioritize posters in your preferred language + original language (VO)
- **Per-library Rules**: Enable/disable rotation by library
- **Orphan Cleanup**: Automatically detect and remove pools for deleted media
- Works with Movies, TV Shows, Seasons, Episodes, and Collections

### New in v1.5.0

- **Image Quality Filtering**: Reject low-resolution posters (configurable min width/height)
- **Retry with Backoff**: Automatic retry (1s→2s→4s) on provider/download failures
- **Duplicate Detection**: Optional perceptual hash to skip visually identical posters
- **Pool Purge**: One-click button to delete all .poster_pool directories

---

## Requirements

- Jellyfin **10.11.6 or newer**
- **.NET 9 runtime** on the Jellyfin server
- At least one remote image provider enabled (TMDb, Fanart, etc.)

---

## Installation

### Repository install

1. In **Dashboard → Plugins → Catalog**, add this repository URL:

   ```text
   https://raw.githubusercontent.com/maelmoreau21/jellyfin-plugin-poster-rotator/refs/heads/main/manifest.json
   ```

2. Install **Poster Rotator** from **My Plugins**.
3. Open the plugin tile to configure settings.
4. Restart Jellyfin once the install completes.

### Manual install

1. Download the latest release ZIP from the **Releases** page.
2. Stop the Jellyfin server.
3. Extract and copy `Jellyfin.Plugin.PosterRotator.dll` into your plugins folder:
   - Windows: `C:\ProgramData\Jellyfin\Server\plugins`
   - Linux: `/var/lib/jellyfin/plugins`
   - Docker: mount a host directory as `/config/plugins`
4. Start Jellyfin and verify the plugin appears under **Dashboard → Plugins**.

---

## How It Works

When the scheduled task runs:

1. Creates a `.poster_pool` directory next to each media file
2. Downloads posters from enabled remote providers until pool is full
3. Applies language filtering (preferred language + original language)
4. Checks image quality and rejects low-resolution images (v1.5.0)
5. Detects and skips visual duplicates if enabled (v1.5.0)
6. Rotates to the next image (sequential or random)
7. Updates the primary poster and nudges Jellyfin's library watchers

---

## Language Preferences

Configure poster language selection:

- **Preferred Language**: Your primary language (e.g., "fr" for French)
- **Max Preferred Images**: Limit posters in preferred language (e.g., 2)
- **Original Language (VO)**: Auto-detect or set fallback language
- **Include Unknown**: Whether to include posters without language info

Example: With `PreferredLanguage=fr` and `MaxPreferredLanguageImages=2`:
- French movie → 2 French posters + 3 French (VO) posters
- Japanese anime → 2 French posters + 3 Japanese (VO) posters

---

## Settings Overview

| Setting | Description |
|---------|-------------|
| **Pool Size** | Number of posters per item (default: 5) |
| **Sequential Rotation** | Deterministic order instead of random |
| **Min Hours Between Switches** | Cooldown before rotating again (default: 23) |
| **Lock After Fill** | Freeze pool once full |
| **Language Filter** | Enable language-based selection |
| **Min Image Width / Height** | Reject images below these dimensions (v1.5.0) |
| **Duplicate Detection** | Toggle perceptual hash dedup at download (v1.5.0) |
| **Auto Cleanup** | Remove orphaned pools automatically |
| **Cleanup Interval** | Days between auto-cleanup runs |
| **🗑 Purge All Pools** | Delete ALL .poster_pool directories (v1.5.0) |

---

## Files Created Per Item

```
/path/to/movie/
├── movie.mkv
├── poster.jpg
└── .poster_pool/
    ├── pool_currentprimary.jpg      # Backup of original poster
    ├── pool_1705123456789.jpg       # Downloaded posters
    ├── rotation_state.json          # Rotation history
    ├── pool_languages.json          # Language metadata
    ├── pool_hashes.json             # Perceptual hashes (v1.5.0)
    └── pool.lock                    # Present if locked
```

---

## Scheduled Task

The plugin registers **Rotate Movie Posters (Pool Then Rotate)** under **Dashboard → Scheduled Tasks**. Run it manually or assign a schedule (e.g., nightly).

---

## Troubleshooting

### Pool Never Grows

- Verify TMDb or Fanart providers are enabled
- Check server logs for `PosterRotator` messages

### Posters Don't Change

- Run a library scan after rotation
- Clear browser/app caches
- Restart Jellyfin if needed

### No Providers Detected

- Restart Jellyfin after installing provider plugins
- Confirm media has provider IDs (TMDb ID, etc.)

---

## Version History

- **1.5.0.0** – Image quality filtering, retry with backoff, perceptual duplicate detection, pool purge button. Pool Manager removed.
- **1.3.0.0** – Pool Manager web interface, language preferences, provider image search, orphan cleanup, drag & drop import
- **1.2.0.0** – Jellyfin 10.11 compatibility, season/episode toggles, collection support
- **1.1.0.0** – Initial fork with per-library selection

---

## Build From Source

```powershell
dotnet build src/Jellyfin.Plugin.PosterRotator/Jellyfin.Plugin.PosterRotator.csproj -c Release
```
