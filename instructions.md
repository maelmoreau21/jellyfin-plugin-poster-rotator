# Instructions

## Goal of Branch 1.8

Prepare Poster Rotator `1.8.0.0` for Jellyfin `12.0.0.0`.

- Do not add raw SQL access.
- Do not use `SQLiteConnection`, `DbConnection`, `FromSql`, `ExecuteSql`, or raw textual queries.
- Use injected Jellyfin services (`ILibraryManager`, `IProviderManager`, etc.).
- Keep `CS0618` as an error to block `[Obsolete]` APIs.
- Keep `1.6.0.0` as the compatible line for Jellyfin `10.11.x`.
- Keep `1.7.0.0` as the previous Jellyfin 12 beta line.
- Keep the plugin interface localizable in English and French, falling back to English.

## Pool Storage

Line 1.8 uses structured file storage in a dedicated folder, sister to the plugin's config folder:

- `Jellyfin.Plugin.PosterRotator.pools/index.json`: lightweight index for diagnostics, search, pagination, and global actions.
- `Jellyfin.Plugin.PosterRotator.pools/{itemId}/pool.json`: versioned pool metadata, images, hashes, languages, sources, dates, and recent errors.
- Legacy pools under `Jellyfin.Plugin.PosterRotator/pools` are ignored and must not be migrated.
- Legacy files `rotation_state.json`, `pool_urls.json`, `pool_languages.json`, and `pool_hashes.json` must no longer be automatically migrated.

The UI search must not scan all folders on each request. Use the existing index and automatically rebuild it only if it is missing or unreadable while pool folders exist. After a complete purge, invalidate the cache and accept a clean empty list. The endpoint `POST /PosterRotator/Pools/RebuildIndex` remains available internally/for admins, but no visible button should be exposed for this action.

Do not add a custom `DbContext` or raw SQL for this storage.

## Rotation and Large Library Download

Scheduled tasks must remain suitable for libraries with over 200,000 media items:

- `Download missing pools` retrieves IDs using `ILibraryManager.GetItemIds`;
- `Rotate pools` reads `Jellyfin.Plugin.PosterRotator.pools/index.json` and does not scan the entire library;
- Shuffle IDs or index entries before processing to distribute changes across large libraries;
- Process by `ProcessingBatchSize`;
- Resolve items at the time of processing;
- Respect `MinHoursBetweenSwitches` before any rotation;
- Cap each execution with `MaxRotationsPerRun`, `MaxProviderLookupsPerRun`, and `MaxDownloadsPerRun`;
- Interpret `MaxRotationsPerRun = 0` as "no rotation count limit", without ignoring the cooldown;
- Do not download or create empty pools from `Rotate pools`;
- Only create `Jellyfin.Plugin.PosterRotator.pools/{itemId}` during `Download missing pools` via `PoolStore` when a valid image file is about to be written, and delete the folder if it remains empty;
- Never fall back to `.poster_pool` or the media folder when `Download missing pools` forces `PluginData` storage;
- Ignore and clear legacy cached `ManualLibraryRoots` during admin or scheduled runs, as the UI no longer exposes them;
- Keep `PluginData` as the recommended storage.

Default values:

- `PoolSize`: `4`
- `MinHoursBetweenSwitches`: `72`
- `MaxRotationsPerRun`: `500`
- `MaxProviderLookupsPerRun`: `250`
- `MaxDownloadsPerRun`: `250`
- `ProcessingBatchSize`: `250`
- `CadenceProfile`: `Balanced`

Duplicate detection must use a hash calculated after normalization by `IImageProcessor` when this Jellyfin service is available, falling back to the lightweight file hash. Legacy hashes remain accepted, but new downloads and imports should favor the normalized hash.

## Admin API

All `PosterRotator/*` routes must remain protected by `RequiresElevation`.

- `GET /PosterRotator/Diagnostics`
- `GET /PosterRotator/Pools?library=&query=&type=&hasErrors=&isEmpty=&start=&limit=`
- `POST /PosterRotator/Pools/RebuildIndex` (internal/admin, no visible button)
- `POST /PosterRotator/Pools/DownloadMissing`
- `GET /PosterRotator/Pools/{itemId}`
- `GET /PosterRotator/Pools/{itemId}/Images/{fileName}`
- `GET /PosterRotator/Pools/{itemId}/Images/{fileName}?preview=true&maxWidth=320&maxHeight=480&quality=80`
- `POST /PosterRotator/Pools/{itemId}/RotateNow`
- `POST /PosterRotator/Libraries/{libraryName}/RotateNow`
- `POST /PosterRotator/Pools/{itemId}/Images`
- `DELETE /PosterRotator/Pools/{itemId}/Images/{fileName}`
- `POST /PosterRotator/Purge`

The `GET /PosterRotator/Pools/{itemId}` response also exposes the current poster:

- `CurrentPoster.PrimaryImageFound`
- `CurrentPoster.Matched`
- `CurrentPoster.FileName`
- `CurrentPoster.MatchMethod`
- `Images[].IsCurrent`

Detection first attempts the hash of the current primary Jellyfin image, falling back to the last applied image (`LastAppliedUtc`) if no hash matches.

## Scheduled Tasks

Jellyfin must display a `Poster Rotator` category in the scheduler:

- `Download missing pools`: daily task defaulting to 02:00, stable key `PosterRotator.DownloadMissingPoolsTask`;
- `Rotate pools`: daily task defaulting to 03:00, stable key `PosterRotator.RotatePostersTask`, rotation-only without download;
- `Orphan pool cleanup`: weekly task defaulting to purge `Scope = "orphans"`.
- Task names and descriptions can be localized, but keys must remain stable.

Legacy automatic cleanup options remain in the configuration model for compatibility but must no longer be exposed in the interface.

## Interface

The interface uses two true ARIA tabs: `Pools` and `Parameters`.

- The first control in the `Parameters` tab is the global interface language `InterfaceLanguage`: `auto`, `en`, `fr`;
- `auto` follows Jellyfin's `ServerConfiguration.UICulture`, and any unsupported language falls back to English;
- `Pools` is active by default, with `SettingsPanel` hidden by `hidden`;
- Search and filters only in the `Pools` tab;
- Library filter as a dropdown loaded from `/Library/VirtualFolders`;
- Compact statistics;
- Paged table of pools with size `25 / 50 / 100 / 200`;
- Dense result table with name, path, or ID, and readable type/library badges;
- JS request token to prevent an older search from overwriting a newer one;
- Detail panel with thumbnails loaded via `ApiKey` and `preview` parameters;
- Opening a pool must not reload the entire list; reload the list only after rotation, import, deletion, or purge;
- Server-side scaled thumbnails via `IImageProcessor.ProcessImage`, normalized to poster format, bounded size, with fallback `Preview unavailable` hidden by default and visible only on load error;
- If the server preview fails, the image retries the original route once, still bounded by CSS and `width`/`height` attributes, before displaying `Preview unavailable`;
- Image cards must remain small, about `104x156`, to view multiple posters on screen;
- Poster file names must wrap to 2 or 3 lines using `overflow-wrap:anywhere`;
- The current poster must display an `Active` badge;
- Delete/import images;
- Main action `Download missing pools` which calls `POST /PosterRotator/Pools/DownloadMissing`;
- Do not display a `Repair pool list` button; index repair is automatic or reserved for the admin endpoint;
- Action `Delete all pools` which calls `POST /PosterRotator/PurgeAllPools` after confirmation;
- Buttons `Previous`, `Next`, `Library rotation`, `Purge library`, and `Purge media` must be disabled when their action is unavailable;
- The `Parameters` tab exposes only settings useful on a daily basis;
- The field `Maximum number of posters to change per run` accepts `0` for no count limit, with help text in the same `inputContainer` just below the label;
- Languages expose a configurable fallback order: original then configured, configured then original, original only, or configured only;
- The JS helper `fallbackModeValue` must accept `0..3` and the names `OriginalThenConfigured`, `ConfiguredThenOriginal`, `OriginalOnly`, `ConfiguredOnly`;
- `Sequential rotation` must be labeled `Browse posters in order` with the help text: `Enabled: takes the next image from the pool on each rotation. Disabled: chooses a poster at random. Does not change the delay between two rotations.`;
- Last resort all languages can be enabled separately;
- Do not display `CadenceProfile`, `PoolSize`, `MinHoursBetweenSwitches`, `MaxProviderLookupsPerRun`, `MaxDownloadsPerRun`, `ProcessingBatchSize`, `AutoCleanupOrphanedPools`, or `CleanupIntervalDays`;
- Do not display `ManualLibraryRoots`; clear this list when saving from the interface.

## Local Build

The default target of the project is:

```text
12.0.0-20260523021143
```

This version is published on GitHub Packages Jellyfin and requires an authenticated NuGet configuration.

Expected command for Jellyfin 12:

```powershell
dotnet restore .\jellyfin-plugin-poster-rotator.sln -p:JellyfinPackageVersion=12.0.0-20260523021143 --source https://api.nuget.org/v3/index.json --source https://nuget.pkg.github.com/jellyfin/index.json
dotnet build .\jellyfin-plugin-poster-rotator.sln -c Release --no-restore -p:JellyfinPackageVersion=12.0.0-20260523021143 -warnaserror:CS0618
dotnet test .\jellyfin-plugin-poster-rotator.sln -c Release --no-restore -p:JellyfinPackageVersion=12.0.0-20260523021143 -warnaserror:CS0618
```

Public fallback to verify the code without GitHub Packages access:

```powershell
dotnet build .\jellyfin-plugin-poster-rotator.sln -c Release -p:JellyfinPackageVersion=10.11.10 -warnaserror:CS0618
dotnet test .\jellyfin-plugin-poster-rotator.sln -c Release -p:JellyfinPackageVersion=10.11.10 -warnaserror:CS0618
```

## Release

1. Verify that `Version`, `AssemblyVersion`, and `FileVersion` are `1.8.0.0`.
   - Do not change `meta.json.version`, `manifest.json.version`, or `targetAbi` for UI hotfixes on this line.
2. Compile in `Release` against the authenticated Jellyfin 12 package.
3. Run tests.
4. Create `Jellyfin.Plugin.PosterRotator-1.8.0.0.zip` containing:
   - `Jellyfin.Plugin.PosterRotator.dll`
   - `Jellyfin.Plugin.PosterRotator.deps.json`
   - `Jellyfin.Plugin.PosterRotator.pdb`
   - `jellyfin-plugin-posterrotator.png`
   - `meta.json`
5. Calculate the MD5 of the zip and report it in `manifest.json`.
6. Keep the `1.7.0.0` and `1.6.0.0` manifest entries for previous lines.

## Cleanup

Files not to commit:

- `bin/`
- `obj/`
- `artifacts/`
- temporary packaging files

The release zip at the root can be kept only when it matches a version declared in `manifest.json`.

## Useful Verifications

```powershell
rg -n "SQLite|Sqlite|SQLiteConnection|DbConnection|DbContext|FromSql|ExecuteSql|SELECT |INSERT |UPDATE |DELETE |System\.Data|Microsoft\.Data|RawSql" src tests
rg -n "Obsolete|GetImageProviders|IRemoteImageProvider|SetLastWriteTimeUtc" src tests
Get-Content .\manifest.json | ConvertFrom-Json | Out-Null
```

## Behavior to Preserve

- `PluginData` storage must remain the recommended mode.
- `MediaFolders` mode must remain available only for compatibility.
- Legacy `.poster_pool` folders remain available only for `MediaFolders` mode; do not automatically migrate them to PluginData.
- Remote downloads must remain bounded in size and block private/local URLs by default.
- The interface must only edit `Jellyfin.Plugin.PosterRotator.pools`; `MediaFolders` pools remain a compatibility mode.
- Upload, deletion, and immediate rotation actions must use a per-pool lock.
- Remote downloads must disable automatic redirects and re-validate each redirect target before reading the response.
- Uploads must reject `IFormFile.Length` above `MaxDownloadMegabytes` before `OpenReadStream`.
