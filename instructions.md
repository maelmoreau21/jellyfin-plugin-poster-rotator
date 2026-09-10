# Instructions

## Goal of Branch 1.8

Prepare Poster Rotator `1.8.5.2` for Jellyfin `12.0.0.0`.

- Backwards compatibility is not required or maintained: the only goal is to work seamlessly with the current version on Jellyfin 12 (`12.0.0.0`).
- Do not maintain legacy Jellyfin lines (Jellyfin 10.11 / 1.6.0.0) or previous Jellyfin 12 iterations.
- Do not add raw SQL access.
- Do not use `SQLiteConnection`, `DbConnection`, `FromSql`, `ExecuteSql`, or raw textual queries.
- Use injected Jellyfin services (`ILibraryManager`, `IProviderManager`, etc.).
- Keep `CS0618` as an error to block `[Obsolete]` APIs.
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
- `MinHoursBetweenSwitches`: `0` (managed by scheduler, 0 = rotation allowed on each run)
- `MaxRotationsPerRun`: `500`
- `MaxProviderLookupsPerRun`: `250`
- `MaxDownloadsPerRun`: `250`
- `ProcessingBatchSize`: `250`
- `CadenceProfile`: `Balanced`

Duplicate detection must use a hash calculated after normalization by `IImageProcessor` when this Jellyfin service is available, falling back to the lightweight file hash. Legacy hashes remain accepted, but new downloads and imports should favor the normalized hash.

## Admin API

All `PosterRotator/*` routes must remain protected by `RequiresElevation`.

- `GET /PosterRotator/Localization?language=`
- `GET /PosterRotator/Diagnostics`
- `GET /PosterRotator/Pools?library=&query=&type=&hasErrors=&isEmpty=&completion=&isLocked=&sortBy=&sortOrder=&start=&limit=`
- `GET /PosterRotator/Pools/DownloadStatus`
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
- `POST /PosterRotator/PurgeAllPools`

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
- Search and filters only in the `Pools` tab (search keyword, media type, completion status, page size, sort by, and sort order);
- Libraries are configured in the `Parameters` tab; do not display a library multiselect filter in the `Pools` tab toolbar;
- Status filter unified into `PoolsCompletion` (`all`, `complete`, `incomplete`, `empty`, `errors`), consolidating completion states and error states while eliminating redundant state dropdowns;
- Do not display locking controls or lock filters in the interface (`PoolsLock` and `Lock full pools` are eliminated);
- Sorting controls: `PoolsSortBy` (`updated`, `name`, `images`, `lastrotated`) and `PoolsSortOrder` (`desc`, `asc`);
- Live download progress banner with percentage indicator and fill bar, polling `GET /PosterRotator/Pools/DownloadStatus`;
- Compact statistics (Pools count, Disk space, Orphans count, Current page);
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
- Delete/import images individually;
- Action `Delete this pool` (`#DeleteCurrentPoolBtn`) directly in the media detail panel calling `POST /PosterRotator/Purge` with scope `item`;
- Main action `Download missing pools` which calls `POST /PosterRotator/Pools/DownloadMissing`;
- Do not display a `Repair pool list` button; index repair is automatic or reserved for the admin endpoint;
- Action `Delete all pools` which calls `POST /PosterRotator/PurgeAllPools` after confirmation;
- Buttons `Previous` and `Next` must be disabled at pagination bounds; `Purge media` and `Delete this pool` are disabled when no pool is selected;
- The `Parameters` tab exposes only settings useful on a daily basis:
  - `InterfaceLanguage` (`auto`, `en`, `fr`);
  - `PoolSize`: target number of posters per media (1 to 50, default `4`);
  - `PoolStorageMode`: storage location (PluginData or Media folders);
  - `MaxRotationsPerRun`: maximum number of posters to change per run (accepts `0` for no count limit, with help text in the same `inputContainer` just below the label);
  - `MaxDownloadsPerRun`: maximum poster downloads per run (accepts `0` for no limit, default `250`);
  - Target media types checkboxes: Movies, Series, Collections / Sagas, Seasons, Episodes;
  - Behavior checkboxes: `Sequential rotation` (labeled `Browse posters in order` with help text: `Enabled: takes the next image from the pool on each rotation. Disabled: chooses a poster at random. Does not change the delay between two rotations.`), `Block private URLs`, `Visual duplicates`;
  - Configurable library selection with Select All / Deselect All shortcuts and selection counter badge;
  - Image quality and download limits: `MaxDownloadMegabytes`, `MinImageWidth`, `MinImageHeight`;
  - Language filtering: `EnableLanguageFilter`, `PreferredLanguage`, `MaxPreferredLanguageImages` (0 to 10), `FallbackLanguage`, configurable fallback order (`OriginalThenConfigured`, `ConfiguredThenOriginal`, `OriginalOnly`, `ConfiguredOnly`), `IncludeUnknownLanguage`, and `AllowAnyLanguageFallback`;
  - Dynamic opacity/dimming on language filter options when language filtering is disabled;
- The JS helper `fallbackModeValue` must accept `0..3` and the names `OriginalThenConfigured`, `ConfiguredThenOriginal`, `OriginalOnly`, `ConfiguredOnly`;
- The dead field `ExtraPosterPatterns` must not be displayed in the interface;
- Do not display technical or internal fields: `MinHoursBetweenSwitches` (rotation timing is managed by the Jellyfin task scheduler), `LockImagesAfterFill`, `CadenceProfile`, `MaxProviderLookupsPerRun`, `ProcessingBatchSize`, `AutoCleanupOrphanedPools`, or `CleanupIntervalDays`;
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

Standard build and test commands for Jellyfin 12:

```powershell
dotnet restore .\jellyfin-plugin-poster-rotator.sln
dotnet build .\jellyfin-plugin-poster-rotator.sln -c Release --no-restore -warnaserror:CS0618
dotnet test .\jellyfin-plugin-poster-rotator.sln -c Release --no-restore -warnaserror:CS0618
```

## Release and Main Branch Protocol

Whenever instructed to push or send changes to the `main` branch (e.g. "envoie dans la branche main", "push sur main", "fais la release") or when preparing a release:

1. **Version consistency**: Ensure the version number (e.g., `1.8.5.2`) is updated consistently across:
   - `src/Jellyfin.Plugin.PosterRotator/Jellyfin.Plugin.PosterRotator.csproj` (`Version`, `AssemblyVersion`, `FileVersion`)
   - `meta.json` (`version`)
   - Documentation (`instructions.md`, `README.md`)
2. **Build and Test**:
   - Compile in `Release` for Jellyfin 12: `dotnet build .\jellyfin-plugin-poster-rotator.sln -c Release -warnaserror:CS0618`
   - Run tests: `dotnet test .\jellyfin-plugin-poster-rotator.sln -c Release -warnaserror:CS0618`
3. **Packaging**:
   - Create the release zip `Jellyfin.Plugin.PosterRotator-<version>.zip` at the repository root containing:
     - `Jellyfin.Plugin.PosterRotator.dll`
     - `Jellyfin.Plugin.PosterRotator.deps.json`
     - `Jellyfin.Plugin.PosterRotator.pdb`
     - `jellyfin-plugin-posterrotator.png`
     - `meta.json`
4. **Manifest update**:
   - Calculate the MD5 checksum of the zip.
   - Update `manifest.json`:
     - Update `version` to the new version.
     - Update `sourceUrl` (`https://github.com/maelmoreau21/jellyfin-plugin-poster-rotator/releases/download/v<version>/Jellyfin.Plugin.PosterRotator-<version>.zip`).
     - Update `checksum` with the MD5 hash.
     - Update `timestamp` (UTC ISO 8601).
     - Update `changelog` with the summary of changes.
     - Only keep the current version in `manifest.json` (do not keep previous lines; backwards compatibility is not maintained).
5. **Clean obsolete archives**:
   - Delete any older zip files from the repository root and `artifacts/` (only the current release zip matching `manifest.json` may remain).
6. **Git & GitHub Release**:
   - Commit all changes (code, manifest, instructions, meta, etc.).
   - Push commits to the `main` branch.
   - Tag the release: `git tag -a v<version> -m "Release v<version>"` and push the tag: `git push origin v<version>`.
   - Publish the GitHub Release with the zip asset attached (e.g. using `gh release create v<version> .\Jellyfin.Plugin.PosterRotator-<version>.zip --title "v<version>" --notes "..."`).

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
