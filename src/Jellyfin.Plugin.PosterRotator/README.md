# Jellyfin Poster Rotator

This project contains the Jellyfin plugin implementation.

## Version

- Plugin version: `1.6.0.0`
- Target ABI: `10.11.0.0`
- Default Jellyfin package baseline: `10.11.0`
- Verified target line: Jellyfin `10.11.0.0` through `10.11.8.0`

## Main Flow

1. Query Jellyfin items for movies, series, collections, and optionally seasons/episodes.
2. Resolve the configured library selection.
3. Resolve each item's pool directory.
4. In default mode, store pools under the plugin data folder.
5. Migrate existing media-folder `.poster_pool` directories into plugin data and remove the legacy directory after success.
6. Top up the pool from Jellyfin remote image providers.
7. Validate URL, response size, image header, dimensions, language, and duplicate hash.
8. Pick the next candidate and apply it with `IProviderManager.SaveImage(...)`.

## Compatibility Builds

```powershell
dotnet build .\src\Jellyfin.Plugin.PosterRotator\Jellyfin.Plugin.PosterRotator.csproj -c Release /p:JellyfinPackageVersion=10.11.0
dotnet build .\src\Jellyfin.Plugin.PosterRotator\Jellyfin.Plugin.PosterRotator.csproj -c Release /p:JellyfinPackageVersion=10.11.8
```
