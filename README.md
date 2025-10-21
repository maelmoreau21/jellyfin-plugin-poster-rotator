
# Jellyfin Poster Rotator

Rotate poster images in Jellyfin by building a local pool of artwork next to each media item, then cycling the primary image on a schedule. This fork tracks the upstream project at [jonah-jamesen/jellyfin-plugin-poster-rotator](https://github.com/jonah-jamesen/jellyfin-plugin-poster-rotator) and adds a handful of quality-of-life fixes and compatibility updates.

---

## Key Features

- Works with both Movies and TV Shows (per-type toggles in settings).
- Per-library enable/disable rules plus optional manual library root overrides.
- Builds a per-item `.poster_pool` folder that caches downloaded images for reuse.
- Sequential or random rotation with a configurable cooldown window.
- Snapshot of the current primary poster is retained so you can roll back.
- Optional pool locking prevents metadata refreshes from replacing curated art.
- Optional rotation support for season and episode artwork (disabled by default).
- Collections/box sets are processed automatically alongside standard libraries.
- Reflection-based library API shim keeps the plugin working on Jellyfin 10.10 and 10.11 without separate builds.

---

## Requirements

- Jellyfin **10.10.3 or newer** (tested with 10.11.0).
- **.NET 8 runtime** on the Jellyfin server.
- At least one remote image provider enabled (TMDb, Fanart, etc.).

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
   - Linux: `/var/lib/jellyfin/plugins` (or `/var/lib/jellyfin/plugins/local`)
   - Docker: mount a host directory as `/config/plugins` and place the DLL there
4. Start Jellyfin and verify the plugin appears under **Dashboard → Plugins**.

---

## How It Works

When the scheduled task runs the plugin:

1. Ensures a `.poster_pool` directory exists next to each media file.
2. Downloads new posters from your enabled remote providers until the pool size is satisfied.
3. Falls back to the current primary artwork if no remote art is found.
4. Rotates to the next image (sequentially or randomly) while respecting the cooldown.
5. Updates the primary poster file on disk and nudges Jellyfin's library watchers.

Because Jellyfin and clients cache aggressively, you may still need to run a library scan or clear client caches to see changes immediately.

### Supported Item Types

- **Movies & TV series** – toujours traités, selon les bibliothèques sélectionnées.
- **Collections / box sets** – automatiquement incluses lorsque la bibliothèque correspondante est cochée.
- **Saisons** – activer la case « Inclure les saisons » dans les paramètres du plugin pour les ajouter à la rotation.
- **Épisodes** – activer la case « Inclure les épisodes » pour gérer les miniatures d’épisodes.

### Files Created Per Item

- `.poster_pool/pool_currentprimary.<ext>` – snapshot of the current primary poster
- `.poster_pool/pool_<timestamp>.<ext>` – cached poster candidates
- `.poster_pool/rotation_state.json` – rotation history and cooldown tracking
- `.poster_pool/pool.lock` – present when **Lock After Fill** is enabled

---

## Settings Overview

- **Pool Size** – number of posters to keep cached per item.
- **Sequential Rotation** – enables deterministic rotation order; otherwise randomised.
- **Min Hours Between Switches** – cooldown before an item can rotate again.
- **Lock After Fill** – freezes the pool after it reaches the configured capacity.
- **Extra Poster Patterns** – comma-separated filename globs (e.g. `cover.jpg, poster_alt-*.png`).
- **Trigger Library Scan After Rotation** – optional best-effort nudge for Jellyfin's scanner.
- **Inclure les saisons** – active la rotation des affiches des saisons.
- **Inclure les épisodes** – active la rotation des vignettes d'épisodes.
- **Library Rules** – enable/disable by library name or provide manual roots when auto-detection misses a share.

Tip: You can run the rotation task more frequently than the cooldown; items still within the configured window are skipped automatically.

---

## Scheduled Task

The plugin registers **Rotate Movie Posters (Pool Then Rotate)** under **Dashboard → Scheduled Tasks**. Run it manually or assign a schedule (e.g. nightly). Pairing it with a post-rotation library scan often helps clients pick up the new images faster.

---

## Troubleshooting

### Pool Never Grows Beyond One Image

- Verify remote providers such as TMDb or Fanart are installed, enabled, and reachable.
- Check the Jellyfin server log for `PosterRotator` messages describing the failure.

### Posters Do Not Change in the UI

- Run the matching library scan from **Scheduled Tasks** to force Jellyfin to notice the file change.
- Open the media item, go to **Images**, and click **Refresh** to rebuild the artwork index.
- Clear browser/app caches or use a private window; cached images may persist client-side.
- As a last resort, restart Jellyfin which forces all cached artwork to refresh.

### No Providers Detected

- Restart Jellyfin after installing new provider plugins.
- Confirm the media item has provider IDs so lookups succeed.

---

## Version History

- **1.2.0.0** – Jellyfin 10.11 compatibility via reflection-based enumeration, configuration cleanup, season/episode rotation toggles (off by default), automatic collection support, and documentation refresh.
- **1.1.0.0** – Initial fork with per-library selection UI and rotation tweaks.

---

## Build From Source

```powershell
dotnet build src/Jellyfin.Plugin.PosterRotator/Jellyfin.Plugin.PosterRotator.csproj -c Release
```
