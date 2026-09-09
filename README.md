# Jellyfin Poster Rotator

<p align="center">
	<img src="jellyfin-plugin-poster-rotator.png" width="128" height="128" alt="Poster Rotator logo">
</p>

<p align="center">
	<img src="https://img.shields.io/github/license/maelmoreau21/jellyfin-plugin-poster-rotator" alt="License">
	<img src="https://img.shields.io/github/v/release/maelmoreau21/jellyfin-plugin-poster-rotator" alt="Release">
</p>

<p align="center">
	<strong>Creates local poster pools per media item and rotates them on a schedule, with language and duplicate filtering.</strong>
</p>

## Overview

Poster Rotator keeps the Jellyfin interface alive by creating a pool of posters per media, then periodically rotating the primary image. The `1.8.4.0` line is optimized for large libraries, including indexes with `200,000+` pools, targets Jellyfin 12 stable, and adds a bilingual English/French interface with full multi-library selection.

## Compatibility

- Plugin version: `1.8.4.0`
- Target ABI: Jellyfin `12.0.0.0`
- Jellyfin packages: `12.0.0` (stable) with `10.11.11` fallback
- Runtime: `.NET 10` (with `.NET 9` compatibility)
- Previous Jellyfin 12 lines: `1.8.3.0`, `1.8.2.0`, `1.8.1.0`, `1.8.0.0`, `1.7.0.0`
- Jellyfin 10.11 line: `1.6.0.0`

Version `1.8.4.0` does not make raw SQL access. It uses Jellyfin services (`ILibraryManager`, `IProviderManager`) and stores its state in the plugin's data folder.

## Features

1. The scheduled task **Download missing pools** retrieves the IDs of movies, shows, collections, and optionally seasons/episodes.
2. The IDs are shuffled and then processed in batches to avoid loading an entire library into memory.
3. Enabled libraries are resolved by the checked libraries in the interface; old hidden manual roots are ignored at runtime.
4. Each media uses a local pool under `Jellyfin.Plugin.PosterRotator.pools/{itemId}`.
5. Remote posters are retrieved from `IProviderManager.GetAvailableRemoteImages(...)`.
6. Images are validated: URL, size, format, dimensions, language, and duplicates.
7. The task **Rotate pools** reads `index.json` and only applies images already present with `IProviderManager.SaveImage(...)`, and only if the media's cooldown has expired.
8. State is maintained in `Jellyfin.Plugin.PosterRotator.pools/index.json` and `Jellyfin.Plugin.PosterRotator.pools/{itemId}/pool.json`.

Duplicate detection uses a hash calculated after normalization with the Jellyfin image pipeline when available. This makes the comparison less sensitive to dimensions, metadata, and encoding differences between providers.

The Jellyfin scheduler displays a **Poster Rotator** section with:

- **Download missing pools**: fills missing or incomplete pools without changing posters, writing strictly under `Jellyfin.Plugin.PosterRotator.pools`;
- **Rotate pools**: rotates eligible posters from existing pools without downloading;
- **Orphan pool cleanup**: deletes pools for media that no longer exist.

The only volume setting exposed in the interface is **Maximum number of posters to change per run**. `0` means no count limit, while still respecting the internal delay between two changes of the same media.
Internal download caps remain active per run; if a run reaches this limit, the interface indicates that the download is progressive and can be restarted.

## Installation

Add this repository to Jellyfin:

```text
https://raw.githubusercontent.com/maelmoreau21/jellyfin-plugin-poster-rotator/refs/heads/main/manifest.json
```

Then install **Poster Rotator** from the plugin catalog and restart Jellyfin.

For a manual zip install, the `Jellyfin.Plugin.PosterRotator-1.8.4.0.zip` archive also contains `meta.json` and `jellyfin-plugin-posterrotator.png`, so that Jellyfin's plugin page can display the plugin image.

## Usage

The admin interface is organized into two separate tabs: `Pools` opens by default and contains only pool tools, and `Parameters` contains only settings. At the top of `Parameters`, the interface language choice offers `Same as Jellyfin`, `English`, and `Francais`; the automatic mode follows the Jellyfin server's `UICulture` language and falls back to English if it is not yet translated.

- Statistics and status in the pools tab, with diagnostics limited to IDs present in the plugin's index;
- Paged search of `PluginData` pools, served from a cached index and adapted to very large volumes;
- Page size options `25 / 50 / 100 / 200` and navigation buttons disabled when not applicable;
- Filters by Jellyfin library, type, error, and empty/non-empty pool;
- Detail of the selected pool showing the currently used poster, an `Active` badge on the corresponding card, and a fallback to the last applied rotation if the hash doesn't match;
- Server-side scaled thumbnails, normalized to the poster format, bounded size, compact grid, and file names wrapping to multiple lines to display more posters on screen;
- **Download missing pools** action to immediately restart filling after a deletion or purge;
- Import and deletion of posters;
- Immediate rotation by media or library;
- Purge of orphans, a library, or a media;
- Delete all pools in a confirmed admin action;
- Simple setting for the maximum number of posters changed per run, with help text `0 = no limit...` directly under the field label;
- **Browse posters in order** setting: when enabled, it takes the next image from the pool on each rotation; when disabled, it chooses a poster at random;
- Libraries, poster languages, and security in the `Parameters` tab;
- Interface translated into English and French, with scheduled task names/descriptions translated according to the same language choice when Jellyfin refreshes these labels;
- Configurable language fallback: preferred language, original language detected, fallback language, images without language, and last resort all languages. The interface also accepts Jellyfin enum names to correctly preserve the fallback order.

Legacy `.poster_pool` folders from `MediaFolders` mode remain compatible only if that mode is selected. Legacy pools under `Jellyfin.Plugin.PosterRotator/pools` are not migrated and are ignored.

## Storage

- `Jellyfin.Plugin.PosterRotator.pools/index.json`: lightweight index for search, pagination, and global actions.
- `Jellyfin.Plugin.PosterRotator.pools/{itemId}/pool.json`: versioned pool metadata, images, hashes, languages, sources, dates, and recent errors.
- Legacy files `rotation_state.json`, `pool_urls.json`, `pool_languages.json`, and `pool_hashes.json` are no longer automatically migrated.

The pool search reads the existing index without scanning all directories and keeps this index in memory until its next modification. If the index is missing or unreadable while pool folders exist, it is rebuilt automatically once; there is no longer a visible manual button for this repair. If media do not yet have a pool, use the scheduled task **Download missing pools** or the **Download missing pools** button in the `Pools` tab. Admin page previews use `preview=true` to load a scaled-down image; if Jellyfin cannot generate this thumbnail, the interface falls back to the original image while keeping it bounded to a small card.

Remote downloading follows a bounded redirection chain and re-validates each target to avoid redirection to localhost, private, or link-local networks when blocking of private URLs is active. Uploads are rejected from the API entry if their size exceeds `MaxDownloadMegabytes`.

## Development

Build, test, and release instructions are in [instructions.md](./instructions.md).

## Binary Publication & Automated GitHub Releases

Releases are published automatically via GitHub Actions:
- Whenever code is pushed or merged to `main`, the `.github/workflows/release.yml` workflow restores, builds, and runs the entire test suite against Jellyfin 12 (`net10.0`) and fallback (`net9.0`).
- If tests pass, it packages `Jellyfin.Plugin.PosterRotator-<version>.zip`, updates `manifest.json` and `meta.json` with the new MD5 checksum and timestamp, commits the manifest updates to `main`, and creates or updates the GitHub Release tag (e.g. `v1.8.4.0`) with the release ZIP attached.

For manual releases or validation:
1. Follow the build/package steps in [instructions.md](./instructions.md) to generate the release zip (e.g., `Jellyfin.Plugin.PosterRotator-1.8.4.0.zip`).
2. Calculate its MD5 checksum and update `manifest.json`.
3. Create the matching release tag (e.g., `v1.8.4.0`) on GitHub and attach `Jellyfin.Plugin.PosterRotator-1.8.4.0.zip`.
4. Ensure the download URL defined in `manifest.json` (`https://github.com/.../releases/download/v1.8.4.0/Jellyfin.Plugin.PosterRotator-1.8.4.0.zip`) matches.

## License

Distributed under the [MIT License](LICENSE).
