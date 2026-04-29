# Jellyfin Poster Rotator

Poster Rotator keeps Jellyfin feeling fresh by rotating primary poster images on a schedule. It builds a small local pool of remote artwork for each movie, series, collection, season, or episode, then asks Jellyfin to save the selected image as the item primary image.

## Compatibility

- Jellyfin `10.11.0.0` through `10.11.8.0`
- .NET 9 runtime on the Jellyfin server
- At least one remote image provider enabled, such as TMDb, TVDB, or Fanart

The plugin is built against Jellyfin `10.11.0` by default and the manifest uses `targetAbi: 10.11.0.0` so the same release can install across the 10.11.x line.

## What Changed in 1.6.0.0

- Default pool storage moved to the plugin data folder instead of media folders.
- Existing `.poster_pool` directories are migrated into plugin data storage and removed after a successful migration.
- Rotation now uses Jellyfin's `IProviderManager.SaveImage(...)` path instead of overwriting `poster.jpg` directly.
- Remote downloads are bounded by a configurable size limit, default `25 MB`.
- Unsafe image URLs pointing at localhost, private IP ranges, or link-local ranges are rejected by default.
- Purge is safer: it skips symlinks/reparse points and only deletes validated pool directories.
- Rotation and purge operations are serialized so they cannot race each other.

## Recommended Setup

Use the default **Plugin data folder** storage mode. It keeps the poster pool outside your media libraries, which means Jellyfin does not need to watch thousands of extra pool files inside movie and series folders. The plugin still changes the visible poster by calling Jellyfin's image save API.

Use **Media folders** only if you specifically want the old `.poster_pool` layout next to your media files.

## Installation

Add this repository in **Dashboard -> Plugins -> Catalog**:

```text
https://raw.githubusercontent.com/maelmoreau21/jellyfin-plugin-poster-rotator/refs/heads/main/manifest.json
```

Then install **Poster Rotator**, restart Jellyfin, and open the plugin settings.

## Settings

- **Pool Size**: number of poster candidates kept per item.
- **Pool Storage**: plugin data folder, recommended, or media folders, legacy.
- **Min Hours Between Switches**: cooldown before an item can rotate again.
- **Sequential Rotation**: stable order instead of random.
- **Lock After Fill**: stop adding new images once a pool reaches the target size.
- **Language Filter**: prioritize preferred language and original-language posters.
- **Min Image Width / Height**: reject low-resolution images.
- **Max Download Size**: reject unexpectedly large remote images.
- **Block Private Network URLs**: reject localhost/LAN/link-local image URLs.
- **Purge All Pools**: remove plugin-data pools and legacy `.poster_pool` directories.

## Build

Baseline compatibility build:

```powershell
dotnet build src/Jellyfin.Plugin.PosterRotator/Jellyfin.Plugin.PosterRotator.csproj -c Release /p:JellyfinPackageVersion=10.11.0
```

Latest 10.11.x verification build:

```powershell
dotnet build src/Jellyfin.Plugin.PosterRotator/Jellyfin.Plugin.PosterRotator.csproj -c Release /p:JellyfinPackageVersion=10.11.8
```

## Security Notes

The plugin downloads only `http` and `https` image URLs from Jellyfin image providers. By default it blocks loopback, private, and link-local targets, applies a byte limit while streaming, checks image headers, and rejects unsupported image content before adding it to a pool.
