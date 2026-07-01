# Jellyfin Poster Rotator - Instructions For AI Agents

Read this document before changing the project.

- Do not invent runtime behavior or payloads: verify the code before proposing changes.
- Do not invent documentation structure: keep this file and [README.md](../../README.md) aligned when behavior or setup changes.
- Do not commit, push, create branches, or merge without an explicit user request.
- Keep changes minimal and preserve the project's existing feature set and naming.

## Canonical Stack

- Language: C# on `net9.0`
- Plugin model: Jellyfin plugin + scheduled task + admin web page
- Storage: local plugin data under the media item / plugin data folders
- Jellyfin APIs: `ILibraryManager`, `IProviderManager`, `IServiceProvider`, `IHttpClientFactory`

## Project Map

- `Jellyfin.Plugin.PosterRotator.csproj`: target framework, package references, plugin metadata
- `Plugin.cs`: plugin registration and web pages
- `Configuration.cs`: persisted options
- `ServiceRegistrator.cs`: dependency injection registration
- `PosterRotatorService.cs`: pool download, rotation, duplicate detection, cleanup flow
- `PosterRotationTask.cs`: Jellyfin scheduled task entry point
- `Helpers/PluginHelpers.cs`: image helpers, retries, atomic JSON utilities
- `Helpers/ImageHash.cs`: perceptual hash and duplicate detection
- `Api/PurgeController.cs`: admin purge endpoint for all pools
- `Web/config.html`: configuration interface

## Runtime Rules

- Rotations must respect cooldowns and the selected ordering mode.
- Pool creation and refresh must keep duplicate detection consistent with the helper code.
- Language filtering and fallback behavior must remain compatible with the saved configuration values.
- Admin purge actions must remain restricted to elevated users.

## Local Data Layout

```
/path/to/movie/
├── movie.mkv
├── poster.jpg
└── .poster_pool/
  ├── pool_original.jpg
  ├── pool_1705123456789.jpg
  ├── rotation_state.json
  ├── pool_languages.json
  ├── pool_urls.json
  ├── pool_hashes.json
  ├── pool_order.json
  └── pool.lock
```

## Validation

- Build the plugin with `dotnet build src/Jellyfin.Plugin.PosterRotator/Jellyfin.Plugin.PosterRotator.csproj` only if the task touches the plugin source tree.
- Prefer checking behavior in the smallest relevant slice before broad validation.
- Update this file when a user-facing workflow, storage format, or setup expectation changes.
