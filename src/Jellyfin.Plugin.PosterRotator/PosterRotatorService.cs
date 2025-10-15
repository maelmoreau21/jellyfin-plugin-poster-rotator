using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

using Jellyfin.Data.Enums;                     // BaseItemKind
using MediaBrowser.Controller.Entities;        // BaseItem
using MediaBrowser.Controller.Entities.Movies; // Movie
using MediaBrowser.Controller.Library;         // ILibraryManager, InternalItemsQuery
using MediaBrowser.Controller.Providers;       // IProviderManager, IRemoteImageProvider
using MediaBrowser.Model.Entities;             // ImageType
using MediaBrowser.Model.Providers;            // RemoteImageInfo
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PosterRotator
{
    public class PosterRotatorService
    {
        private readonly ILibraryManager _library;
        private readonly IProviderManager _providers;
        private readonly IServiceProvider _services;
        private readonly ILogger<PosterRotatorService> _log;

        private static readonly HttpClient _http = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = true
        });

        public PosterRotatorService(
            ILibraryManager library,
            IProviderManager providers,
            IServiceProvider services,
            ILogger<PosterRotatorService> log)
        {
            _library = library;
            _providers = providers;
            _services = services;
            _log = log;
            // No automatic population at startup: manual roots or saved LibraryRules will be used.
        }

        public async Task RunAsync(Configuration cfg, IProgress<double>? progress, CancellationToken ct)
        {
            var q = new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Series },
                Recursive = true
            };

            var items = _library.GetItemList(q).ToList();

            if (cfg.Libraries is { Count: > 0 })
            {
                _log.LogInformation("PosterRotator: library filtering by name is not applied in this build; processing all movies.");
            }

            // Build a quick map: directory -> number of items in that directory.
            // Used to detect "mixed" folders (many items in one folder).
            var dirCounts = items
                .Select(m => Path.GetDirectoryName(m.Path ?? string.Empty) ?? string.Empty)
                .GroupBy(d => d, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

            var total = items.Count;
            var done = 0;

            // Gather library roots once; nudge each only if anything in that root rotated.
            var libraryMap = GetLibraryRootPaths(); // name -> list of paths
            _log.LogDebug("PosterRotator: discovered library map: {Map}", string.Join("; ", libraryMap.Select(kv => kv.Key + ":" + string.Join(",", kv.Value))));
            var allLibraryRoots = libraryMap.SelectMany(kv => kv.Value).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var rootsToNudge = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // If user specified LibraryRules in cfg, build selected roots to process. Fall back to simple Libraries list for compatibility.
            List<string> selectedRoots = new();
            var configuredNames = new List<string>();
            if (cfg.LibraryRules is { Count: > 0 })
            {
                configuredNames.AddRange(cfg.LibraryRules.Where(r => r.Enabled).Select(r => r.Name));
            }
            else if (cfg.Libraries is { Count: > 0 })
            {
                configuredNames.AddRange(cfg.Libraries);
            }

            if (configuredNames.Count > 0)
            {
                var wanted = new HashSet<string>(configuredNames, StringComparer.OrdinalIgnoreCase);
                foreach (var kv in libraryMap)
                {
                    if (wanted.Contains(kv.Key))
                    {
                        selectedRoots.AddRange(kv.Value);
                    }
                }
                // dedupe
                selectedRoots = selectedRoots.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                _log.LogInformation("PosterRotator: configured libraries: {Cfg}", string.Join(",", configuredNames));
                _log.LogInformation("PosterRotator: selected roots to process: {Roots}", string.Join(",", selectedRoots));
            }

            // If manual roots are provided in the configuration, prefer them (they may be paths or names)
            try
            {
                if (cfg.ManualLibraryRoots != null && cfg.ManualLibraryRoots.Count > 0)
                {
                    var manual = cfg.ManualLibraryRoots.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    var resolved = new List<string>();

                    // Use libraryMap to resolve names -> paths when entry looks like a name
                    foreach (var entry in manual)
                    {
                        // Heuristic: if entry contains a ':' (Windows drive) or a path separator, treat as path
                        if (entry.IndexOf(':') >= 0 || entry.IndexOf('\\') >= 0 || entry.IndexOf('/') >= 0)
                        {
                            resolved.Add(entry);
                            continue;
                        }

                        // Otherwise treat as a library name; try to resolve via libraryMap
                        if (libraryMap.TryGetValue(entry, out var paths) && paths != null && paths.Count > 0)
                        {
                            resolved.AddRange(paths);
                        }
                        else
                        {
                            // If no mapping found, also add the raw name as a fallback (it won't match paths but
                            // will allow admins to use name-based logic elsewhere if desired)
                            _log.LogDebug("PosterRotator: manual library name '{Name}' could not be resolved to paths; keeping raw entry", entry);
                            resolved.Add(entry);
                        }
                    }

                    selectedRoots = resolved.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    _log.LogInformation("PosterRotator: using ManualLibraryRoots resolved to: {Roots}", string.Join(",", selectedRoots));
                }
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "PosterRotator: error resolving ManualLibraryRoots");
            }

            foreach (var item in items)
            {
                ct.ThrowIfCancellationRequested();
                // If Libraries configured, skip items not under the selected roots
                if (selectedRoots.Count > 0)
                {
                    var path = item.Path ?? string.Empty;
                    var inSelected = selectedRoots.Any(r => !string.IsNullOrEmpty(r) && path.StartsWith(r, StringComparison.OrdinalIgnoreCase));
                    if (!inSelected) { _log.LogDebug("PosterRotator: skipping item '{Name}' because it is not under selected libraries", item.Name); progress?.Report(++done * 100.0 / Math.Max(1, total)); continue; }
                }
                try
                {
                    var rotated = await ProcessItemAsync(item, cfg, ct, dirCounts).ConfigureAwait(false);
                    if (rotated)
                    {
                        var path = item.Path ?? string.Empty;
                        var root = allLibraryRoots.FirstOrDefault(r => path.StartsWith(r, StringComparison.OrdinalIgnoreCase));
                        if (!string.IsNullOrEmpty(root))
                        {
                            rootsToNudge.Add(root);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "PosterRotator: error processing \"{Name}\" ({Path})", item.Name, item.Path);
                }

                progress?.Report(++done * 100.0 / Math.Max(1, total));
            }

            // Nudge each affected root once
            foreach (var root in rootsToNudge)
            {
                NudgeLibraryRoot(root, cfg.TriggerLibraryScanAfterRotation);
            }
        }

        // returns true if we actually overwrote the current poster
        private async Task<bool> ProcessItemAsync(BaseItem item, Configuration cfg, CancellationToken ct, IDictionary<string,int> dirCounts)
        {
            var itemDir = GetItemDir(item);
            if (string.IsNullOrEmpty(itemDir) || !Directory.Exists(itemDir))
                return false;

            var mixedFolder = IsMixedFolder(item, dirCounts);

            // Use a per-movie pool inside a shared ".poster_pool" when in mixed folders.
            // Keep the original ".poster_pool" in one-movie-per-folder setups.
            var poolDir = mixedFolder
                ? GetPerItemPoolDir(itemDir, item)
                : Path.Combine(itemDir, ".poster_pool");
            Directory.CreateDirectory(poolDir);

            // ---- read existing pool ------------------------------------------------
            var local = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pat in GetPoolPatterns(cfg))
                foreach (var f in Directory.GetFiles(poolDir, pat))
                    local.Add(f);

            var lockFile = Path.Combine(poolDir, "pool.lock");
            var poolIsLocked = File.Exists(lockFile);

            // ---- cooldown state (gates TOP-UP only, not rotation) ------------------
            var statePath = Path.Combine(poolDir, "rotation_state.json");
            var state = LoadState(statePath);
            var key = item.Id.ToString();
            var now = DateTimeOffset.UtcNow;

            bool haveLast = state.LastRotatedUtcByItem.TryGetValue(key, out var lastEpoch);
            var elapsed = haveLast ? (now - DateTimeOffset.FromUnixTimeSeconds(lastEpoch)) : TimeSpan.MaxValue;
            var minHours = Math.Max(1, cfg.MinHoursBetweenSwitches);

            // allow top-up if: never rotated OR past cooldown OR pool is empty
            bool allowTopUp = !haveLast || elapsed.TotalHours >= minHours || local.Count == 0;

            _log.LogDebug("PosterRotator: \"{Item}\" pool has {Count}/{Target}. Locked:{Locked}. AllowTopUp:{Allow} (elapsed {H:0.0}h, min {Min}h)",
                item.Name, local.Count, cfg.PoolSize, poolIsLocked, allowTopUp, haveLast ? elapsed.TotalHours : -1, minHours);

            // ---- TOP-UP only if allowed by cooldown --------------------------------
            if (!poolIsLocked && local.Count < cfg.PoolSize && allowTopUp)
            {
                var need = cfg.PoolSize - local.Count;
                _log.LogDebug("PosterRotator: attempting top-up of {Need} for \"{Item}\"", need, item.Name);

                var added = await TryTopUpFromProvidersDIAsync(item, poolDir, need, cfg, ct).ConfigureAwait(false);
                if (added.Count < need)
                {
                    var more = await TryTopUpFromProvidersReflectionAsync(item, poolDir, need - added.Count, cfg, ct).ConfigureAwait(false);
                    added.AddRange(more);
                }

                foreach (var f in added) local.Add(f);

                // If user wants to lock after fill, create the lock file when target reached.
                if (!poolIsLocked && cfg.LockImagesAfterFill && local.Count >= cfg.PoolSize)
                {
                    try { File.WriteAllText(lockFile, "locked"); } catch { }
                    poolIsLocked = true;
                    _log.LogInformation("PosterRotator: locked pool for \"{Item}\" at size {Size}.", item.Name, local.Count);
                }
            }
            else if (!poolIsLocked && local.Count < cfg.PoolSize && !allowTopUp)
            {
                _log.LogDebug("PosterRotator: skipping top-up for \"{Item}\" due to cooldown (elapsed {H:0.0}h < {Min}h); will still rotate.", item.Name, elapsed.TotalHours, minHours);
            }
            else if (poolIsLocked && !cfg.LockImagesAfterFill)
            {
                // Unlock if config changed.
                try { File.Delete(lockFile); } catch { }
                poolIsLocked = false;
                _log.LogInformation("PosterRotator: unlocked pool for \"{Item}\" (config changed).", item.Name);
            }

            // ---- bootstrap pool from current primary (or existing per-movie poster) if still empty ----
            if (local.Count == 0)
            {
                var primaryPath = TryCopyCurrentPrimaryToPool(item, poolDir, mixedFolder);
                if (primaryPath != null) local.Add(primaryPath);
            }

            if (local.Count == 0)
            {
                _log.LogDebug("PosterRotator: no candidates available for {Name}; nothing to rotate.", item.Name);
                return false;
            }

            // ---- ROTATE -------------------------------------------------------------
            var files = local.ToList();
                var chosen = PickNextFor(files, item, cfg, poolDir, state);

            // Determine destination path:
            //  - If Jellyfin returns a primary path, prefer that (unique per item).
            //  - Else: in mixed folders, write a per-movie poster filename (avoid shared poster.jpg).
            //          in single-movie folders, keep the original poster.jpg fallback.
            bool rotated = false;
            var currentPrimary = item.GetImagePath(ImageType.Primary);

            string? destinationPath = null;
            var chosenExt = Path.GetExtension(chosen);
            if (string.IsNullOrEmpty(chosenExt)) chosenExt = ".jpg";

            if (!string.IsNullOrEmpty(currentPrimary))
            {
                destinationPath = currentPrimary;
            }
            else
            {
                if (mixedFolder)
                {
                    destinationPath = GetPreferredPerItemPosterPath(item, itemDir, chosenExt);
                }
                else
                {
                    destinationPath = Path.Combine(itemDir, "poster.jpg");
                }
            }

            if (!string.IsNullOrEmpty(destinationPath))
            {
                var dir = Path.GetDirectoryName(destinationPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                SafeOverwrite(chosen, destinationPath);
                rotated = true;

                // Forcer Jellyfin à détecter le changement d'affiche
                try
                {
                    // Modifier la date de modification du fichier d'affiche
                    try
                    {
                        if (!string.IsNullOrEmpty(destinationPath) && File.Exists(destinationPath))
                        {
                            File.SetLastWriteTimeUtc(destinationPath, DateTime.UtcNow);
                        }
                    }
                    catch { }

                    // Tenter d'appeler item.UpdateToRepositoryAsync(ItemUpdateType.ImageUpdate, CancellationToken.None) via reflection
                    try
                    {
                        var itemType = item.GetType();
                        var method = itemType.GetMethod("UpdateToRepositoryAsync", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                        if (method != null)
                        {
                            var parms = method.GetParameters();
                            if (parms.Length == 2)
                            {
                                var enumType = parms[0].ParameterType;
                                object? enumVal = null;
                                if (enumType.IsEnum)
                                {
                                    try { enumVal = Enum.Parse(enumType, "ImageUpdate", true); } catch { enumVal = null; }
                                    if (enumVal == null)
                                    {
                                        var fld = enumType.GetField("ImageUpdate");
                                        if (fld != null) enumVal = fld.GetValue(null);
                                    }
                                }

                                if (enumVal != null)
                                {
                                    method.Invoke(item, new object[] { enumVal, CancellationToken.None });
                                    _log.LogInformation("PosterRotator: '{Name}' affiche mise à jour et Jellyfin notifié", item.Name);
                                }
                            }
                            else if (parms.Length == 1 && parms[0].ParameterType == typeof(CancellationToken))
                            {
                                method.Invoke(item, new object[] { CancellationToken.None });
                                _log.LogInformation("PosterRotator: '{Name}' affiche mise à jour et Jellyfin notifié (single param)", item.Name);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _log.LogDebug(ex, "PosterRotator: impossible de notifier Jellyfin pour '{Name}'", item.Name);
                    }
                }
                catch (Exception ex)
                {
                    _log.LogDebug(ex, "PosterRotator: erreur lors de la notification de Jellyfin pour {Name}", item.Name);
                }

                _log.LogInformation("PosterRotator: rotated \"{Item}\" to {Poster} ({Dest})",
                    item.Name, Path.GetFileName(chosen), Path.GetFileName(destinationPath));
            }

            // Ensure filesystem change is visible: touch the destination file and its parent directory.
            if (rotated)
            {
                try
                {
                    if (!string.IsNullOrEmpty(destinationPath) && File.Exists(destinationPath))
                    {
                        File.SetLastWriteTimeUtc(destinationPath, DateTime.UtcNow);
                    }
                    var itemDirToTouch = itemDir;
                    if (!string.IsNullOrEmpty(itemDirToTouch) && Directory.Exists(itemDirToTouch))
                    {
                        Directory.SetLastWriteTimeUtc(itemDirToTouch, DateTime.UtcNow);
                    }
                }
                catch { }
            }
            // record last-rotated time
            state.LastRotatedUtcByItem[key] = now.ToUnixTimeSeconds();
            SaveState(statePath, state);

            // Best-effort: ask Jellyfin to notice the change. Try reflection-based refresh if available.
            if (rotated)
            {
                try
                {
                    TryRefreshItemViaReflection(item);
                }
                catch (Exception ex)
                {
                    _log.LogDebug(ex, "PosterRotator: refresh via reflection failed for {Item}", item.Name);
                }
            }
            return rotated;
        }

        // Build a minimal MetadataRefreshOptions using reflection (kept if you ever want to refresh metadata)
        private static object? CreateDefaultRefreshOptions(Type mroType)
        {
            try
            {
                var mro = Activator.CreateInstance(mroType);
                mroType.GetProperty("ReplaceAllImages")?.SetValue(mro, false);
                mroType.GetProperty("ImageRefreshMode")?.SetValue(mro, Enum.Parse(mroType.GetProperty("ImageRefreshMode")!.PropertyType, "FullRefresh", ignoreCase: true));
                mroType.GetProperty("MetadataRefreshMode")?.SetValue(mro, Enum.Parse(mroType.GetProperty("MetadataRefreshMode")!.PropertyType, "None", ignoreCase: true));
                return mro;
            }
            catch
            {
                return null;
            }
        }

        // Best-effort: try to call library/metadata refresh methods via reflection so Jellyfin notices changed files.
        private void TryRefreshItemViaReflection(BaseItem item)
        {
            try
            {
                // Look for an IServerApplicationHost or a LibraryManager method to trigger image/metadata refresh.
                var libType = _library.GetType();
                var methods = libType.GetMethods(BindingFlags.Public | BindingFlags.Instance);

                // Common method names to try: RefreshItem, RefreshMetadata, UpdateItem, EnqueueRefresh
                var candidates = methods.Where(m => new[] { "RefreshItem", "RefreshMetadata", "RefreshItemMetadata", "Refresh" , "RefreshLibraryItem" }.Contains(m.Name)).ToList();
                foreach (var m in candidates)
                {
                    try
                    {
                        var ps = m.GetParameters();
                        if (ps.Length == 1 && typeof(BaseItem).IsAssignableFrom(ps[0].ParameterType))
                        {
                            m.Invoke(_library, new object[] { item });
                            _log.LogDebug("PosterRotator: invoked {Method} on library for {Item}", m.Name, item.Name);
                            return;
                        }
                    }
                    catch { }
                }

                // No direct library method; try enqueueing a metadata refresh through any available MetadataRefreshManager
                // Search services (IServiceProvider) for a type with 'Refresh' methods.
                try
                {
                    var sp = _services;
                    if (sp != null)
                    {
                        var refreshType = sp.GetType();
                        // Try common service names via service provider GetService<T>() pattern using reflection
                        // We'll attempt to resolve a type named 'MetadataRefreshService' or similar.
                        var candidateSvc = refreshType.Assembly.GetTypes()
                            .FirstOrDefault(t => t.Name.IndexOf("MetadataRefresh", System.StringComparison.OrdinalIgnoreCase) >= 0
                                || t.Name.IndexOf("ImageRefresh", System.StringComparison.OrdinalIgnoreCase) >= 0);
                        if (candidateSvc != null)
                        {
                            var getSvc = refreshType.GetMethod("GetService");
                            if (getSvc != null)
                            {
                                try
                                {
                                    var svc = getSvc.Invoke(sp, new object[] { candidateSvc });
                                    if (svc != null)
                                    {
                                        var m = candidateSvc.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                                            .FirstOrDefault(x => x.Name.IndexOf("Refresh", System.StringComparison.OrdinalIgnoreCase) >= 0);
                                        if (m != null)
                                        {
                                            // Try invoking with item or item.Id
                                            var ps = m.GetParameters();
                                            if (ps.Length == 1 && ps[0].ParameterType == typeof(string))
                                            {
                                                m.Invoke(svc, new object[] { item.Id.ToString() });
                                                _log.LogDebug("PosterRotator: invoked {Method} on service {Svc} for {Item}", m.Name, candidateSvc.Name, item.Name);
                                                return;
                                            }
                                            else if (ps.Length == 1 && typeof(BaseItem).IsAssignableFrom(ps[0].ParameterType))
                                            {
                                                m.Invoke(svc, new object[] { item });
                                                _log.LogDebug("PosterRotator: invoked {Method} on service {Svc} for {Item}", m.Name, candidateSvc.Name, item.Name);
                                                return;
                                            }
                                        }
                                    }
                                }
                                catch { }
                            }
                        }
                    }
                }
                catch { }
            }
            catch { }
        }

        // --- Provider top-up via DI (instrumented + smarter) ---
        private async Task<List<string>> TryTopUpFromProvidersDIAsync(
            BaseItem item, string poolDir, int needed, Configuration cfg, CancellationToken ct)
        {
            var added = new List<string>();
            try
            {
                var provList = ResolveImageProviders().ToList();

                if (provList.Count == 0)
                {
                    provList = EnumerateRemoteProvidersReflection().ToList();
                    _log.LogDebug("PosterRotator: DI returned 0 providers; reflection enumeration found {Count}: {Names}",
                        provList.Count, string.Join(", ", provList.Select(p => p.GetType().Name)));
                }
                else
                {
                    _log.LogDebug("PosterRotator: DI provider top-up target {Needed} for \"{Item}\" (providers: {Count}: {Names})",
                        needed, item.Name, provList.Count, string.Join(", ", provList.Select(p => p.GetType().Name)));
                }

                // Prefer certain providers (e.g. TheTVDB) by moving them to the front of the list
                provList = provList.OrderByDescending(p => PreferredProviderScore(p.GetType().Name)).ToList();

                foreach (var provider in provList)
                {
                    if (added.Count >= needed) break;

                    try
                    {
                        bool supports = true;
                        IEnumerable<ImageType>? supportedTypes = null;

                        try { supports = provider.Supports(item); } catch { /* ignore */ }
                        try { supportedTypes = provider.GetSupportedImages(item); } catch { /* ignore */ }

                        if (!supports)
                        {
                            _log.LogDebug("PosterRotator: provider {Prov} does not support \"{Item}\"", provider.GetType().Name, item.Name);
                            continue;
                        }

                        var prefersPrimary = supportedTypes?.Contains(ImageType.Primary) == true;

                        // 1) normal call
                        var images = await provider.GetImages(item, ct).ConfigureAwait(false);
                        var gotAny = await Harvest(images, preferPrimary: prefersPrimary).ConfigureAwait(false);

                        // 2) per-type overload via reflection (if nothing yet)
                        if (!gotAny && added.Count < needed)
                        {
                            var pType = provider.GetType();
                            var overload = pType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                                .FirstOrDefault(m =>
                                {
                                    if (m.Name != "GetImages") return false;
                                    var ps = m.GetParameters();
                                    return ps.Length == 3
                                        && typeof(BaseItem).IsAssignableFrom(ps[0].ParameterType)
                                        && ps[1].ParameterType.IsEnum
                                        && ps[2].ParameterType == typeof(CancellationToken);
                                });

                            if (overload != null)
                            {
                                async Task TryType(ImageType t)
                                {
                                    if (added.Count >= needed) return;
                                    try
                                    {
                                        var task = (Task)overload.Invoke(provider, new object[] { item, t, ct })!;
                                        await task.ConfigureAwait(false);
                                        var res = task.GetType().GetProperty("Result")?.GetValue(task) as IEnumerable<RemoteImageInfo>;
                                        await Harvest(res, preferPrimary: (t == ImageType.Primary)).ConfigureAwait(false);
                                    }
                                    catch (Exception ex)
                                    {
                                        _log.LogDebug(ex, "PosterRotator: {Prov}.GetImages(item, {Type}, ct) failed for \"{Item}\"",
                                            pType.Name, t, item.Name);
                                    }
                                }

                                await TryType(ImageType.Primary).ConfigureAwait(false);
                                if (added.Count < needed) await TryType(ImageType.Thumb).ConfigureAwait(false);
                                if (added.Count < needed) await TryType(ImageType.Backdrop).ConfigureAwait(false);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _log.LogDebug(ex, "PosterRotator: provider {Provider} failed for \"{Item}\"",
                            provider.GetType().Name, item.Name);
                    }
                }

                _log.LogInformation("PosterRotator: DI/providers added {Count} image(s) for \"{Item}\"", added.Count, item.Name);
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "PosterRotator: DI provider top-up failed for {Name}", item.Name);
            }

            return added;

            // order + download a batch
            async Task<bool> Harvest(IEnumerable<RemoteImageInfo>? images, bool preferPrimary)
            {
                if (images == null) return false;

                var ordered = (preferPrimary
                        ? images.OrderByDescending(i => i.Type == ImageType.Primary).ThenBy(i => i.ProviderName)
                        : images.OrderBy(i => i.ProviderName))
                    .ToList();

                var gotAny = false;

                // Primary first
                    foreach (var info in ordered.Where(i => i.Type == ImageType.Primary))
                {
                    if (added.Count >= needed) break;
                    	await TryDownloadRemote(info, item, poolDir, cfg, ct, added).ConfigureAwait(false);
                    gotAny = true;
                }

                // then others
                if (added.Count < needed)
                {
                        foreach (var info in ordered.Where(i => i.Type == ImageType.Thumb || i == null || i.Type == ImageType.Backdrop))
                    {
                        if (added.Count >= needed) break;
                            await TryDownloadRemote(info, item, poolDir, cfg, ct, added).ConfigureAwait(false);
                        gotAny = true;
                    }
                }

                return gotAny;
            }

            async Task TryDownloadRemote(RemoteImageInfo info,
                                        BaseItem mv, string dir, Configuration c,
                                        CancellationToken token, List<string> bucket)
            {
                if (info == null) return;
                // Prefer portrait images: if provider supplied width/height and image is landscape, skip.
                if (IsLandscapeFromInfo(info))
                {
                    _log.LogDebug("PosterRotator: skipping landscape image for {Item} (url {Url})", mv.Name, info?.GetType().GetProperty("Url")?.GetValue(info));
                    return;
                }
                var url = info.Url;
                if (string.IsNullOrWhiteSpace(url)) return;

                var ext = GuessExtFromUrl(url) ?? ".jpg";
                var name = $"pool_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}{ext}";
                var full = Path.Combine(dir, name);

                try
                {
                    using var resp = await _http.GetAsync(url, token).ConfigureAwait(false);
                    resp.EnsureSuccessStatusCode();
                    await using var s = await resp.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                    await using var f = File.Create(full);
                    await s.CopyToAsync(f, token).ConfigureAwait(false);

                    bucket.Add(full);
                }
                catch (Exception ex)
                {
                    _log.LogDebug(ex, "PosterRotator: download failed for {Url} ({Item})", url, mv.Name);
                }
            }
        }

        private IEnumerable<IRemoteImageProvider> ResolveImageProviders()
        {
            try
            {
                return (_services.GetService(typeof(IEnumerable<IRemoteImageProvider>))
                        as IEnumerable<IRemoteImageProvider>)
                       ?? Array.Empty<IRemoteImageProvider>();
            }
            catch
            {
                return Array.Empty<IRemoteImageProvider>();
            }
        }

        private IEnumerable<IRemoteImageProvider> EnumerateRemoteProvidersReflection()
        {
            var found = new List<IRemoteImageProvider>();
            try
            {
                var pm = _providers;
                var t = pm.GetType();

                // 1) generic GetProviders<T>()
                var generic = t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                               .FirstOrDefault(m =>
                                    m.IsGenericMethodDefinition &&
                                    m.GetGenericArguments().Length == 1 &&
                                    (m.Name.Contains("GetProviders", StringComparison.OrdinalIgnoreCase) ||
                                     m.Name.Contains("GetAllProviders", StringComparison.OrdinalIgnoreCase)) &&
                                    m.GetParameters().Length == 0);
                if (generic != null)
                {
                    try
                    {
                        var closed = generic.MakeGenericMethod(typeof(IRemoteImageProvider));
                        var res = closed.Invoke(pm, null) as System.Collections.IEnumerable;
                        if (res != null)
                        {
                            foreach (var p in res)
                                if (p is IRemoteImageProvider rp)
                                    found.Add(rp);
                        }
                    }
                    catch { /* ignore */ }
                }

                // 2) properties/fields holding IEnumerable<IRemoteImageProvider>
                foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    try { AddIfEnumerableOfRemote(p.GetValue(pm), found); } catch { }
                }
                foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    try { AddIfEnumerableOfRemote(f.GetValue(pm), found); } catch { }
                }

                return found
                    .GroupBy(x => x.GetType().FullName)
                    .Select(g => g.First())
                    .OrderByDescending(p => PreferredProviderScore(p.GetType().Name))
                    .ToList();
            }
            catch
            {
                // swallow
            }
            return Array.Empty<IRemoteImageProvider>();

            static void AddIfEnumerableOfRemote(object? obj, List<IRemoteImageProvider> bucket)
            {
                if (obj is System.Collections.IEnumerable e)
                {
                    foreach (var item in e)
                    {
                        if (item is IRemoteImageProvider rp)
                            bucket.Add(rp);
                    }
                }
            }
        }

        // --- Provider top-up via REFLECTION (robust + logging) ---
        private async Task<List<string>> TryTopUpFromProvidersReflectionAsync(
            BaseItem item, string poolDir, int needed, Configuration cfg, CancellationToken ct)
        {
            var added = new List<string>();
            try
            {
                var pm = _providers;
                var pmType = pm.GetType();

                // PATH A: ProviderManager.GetRemoteImages(item, query, ct)
                MethodInfo? getRemoteImages = pmType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m =>
                    {
                        if (m.Name != "GetRemoteImages") return false;
                        var ps = m.GetParameters();
                        return ps.Length == 3 && typeof(BaseItem).IsAssignableFrom(ps[0].ParameterType);
                    });

                if (getRemoteImages != null)
                {
                    _log.LogDebug("PosterRotator: using GetRemoteImages on ProviderManager for \"{Item}\"", item.Name);

                    var queryType = getRemoteImages.GetParameters()[1].ParameterType;

                    async Task<int> HarvestWithQuery(object imageTypeEnumValue)
                    {
                        var query = Activator.CreateInstance(queryType)!;
                        queryType.GetProperty("IncludeAllLanguages")?.SetValue(query, true);
                        queryType.GetProperty("ImageType")?.SetValue(query, imageTypeEnumValue);

                        var t = (Task)getRemoteImages.Invoke(pm, new object[] { item, query, ct })!;
                        await t.ConfigureAwait(false);

                        var result = t.GetType().GetProperty("Result")?.GetValue(t) as System.Collections.IEnumerable;
                        return await DownloadFromEnumerable(result, item, poolDir, needed - added.Count, cfg, ct, added).ConfigureAwait(false);
                    }

                    await HarvestWithQuery(ImageType.Primary).ConfigureAwait(false);
                    if (added.Count < needed) await HarvestWithQuery(ImageType.Thumb).ConfigureAwait(false);
                    if (added.Count < needed) await HarvestWithQuery(ImageType.Backdrop).ConfigureAwait(false);

                    _log.LogInformation("PosterRotator: added {Count} images via GetRemoteImages for \"{Item}\"", added.Count, item.Name);
                    return added;
                }

                // PATH B: enumerate remote providers → provider.GetImages(...)
                MethodInfo? getRemoteImageProviders = pmType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m =>
                    {
                        if (m.Name != "GetRemoteImageProviders" && m.Name != "GetImageProviders") return false;
                        var ps = m.GetParameters();
                        return ps.Length == 1 && (typeof(BaseItem).IsAssignableFrom(ps[0].ParameterType) || ps[0].ParameterType.Name.Contains("IHasImages"));
                    });

                if (getRemoteImageProviders == null)
                {
                    _log.LogDebug("PosterRotator: no way to enumerate remote image providers on this server; skipping top-up for \"{Item}\".", item.Name);
                    return added;
                }

                var providersObj = getRemoteImageProviders.Invoke(pm, new object[] { item });
                if (providersObj is not System.Collections.IEnumerable providers)
                {
                    _log.LogDebug("PosterRotator: provider enumeration returned null/invalid; skipping top-up for \"{Item}\".", item.Name);
                    return added;
                }

                _log.LogDebug("PosterRotator: using provider.GetImages reflection for \"{Item}\"", item.Name);

                async Task HarvestProviderAsync(object provider, object imageTypeEnumValue)
                {
                    if (added.Count >= needed) return;

                    var pType = provider.GetType();
                    var getImagesCandidates = pType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                        .Where(m => m.Name == "GetImages" && typeof(Task).IsAssignableFrom(m.ReturnType))
                        .ToList();

                    foreach (var m in getImagesCandidates)
                    {
                        if (added.Count >= needed) break;

                        var ps = m.GetParameters();
                        object? taskObj = null;

                        try
                        {
                                if (ps.Length == 3 &&
                                typeof(BaseItem).IsAssignableFrom(ps[0].ParameterType) &&
                                ps[1].ParameterType.IsEnum &&
                                ps[2].ParameterType == typeof(CancellationToken))
                            {
                                taskObj = m.Invoke(provider, new object[] { item, imageTypeEnumValue, ct });
                            }
                            else if (ps.Length == 2 &&
                                     typeof(BaseItem).IsAssignableFrom(ps[0].ParameterType) &&
                                     ps[1].ParameterType == typeof(CancellationToken))
                            {
                                taskObj = m.Invoke(provider, new object[] { item, ct });
                            }
                            else if (ps.Length == 3 &&
                                     typeof(BaseItem).IsAssignableFrom(ps[0].ParameterType) &&
                                     ps[2].ParameterType == typeof(CancellationToken))
                            {
                                var queryObj = Activator.CreateInstance(ps[1].ParameterType)!;
                                ps[1].ParameterType.GetProperty("IncludeAllLanguages")?.SetValue(queryObj, true);
                                var imgProp = ps[1].ParameterType.GetProperty("ImageType");
                                if (imgProp != null) imgProp.SetValue(queryObj, imageTypeEnumValue);
                                taskObj = m.Invoke(provider, new object[] { item, queryObj, ct });
                            }
                            else
                            {
                                continue;
                            }

                            if (taskObj is Task t)
                            {
                                await t.ConfigureAwait(false);
                                var result = t.GetType().GetProperty("Result")?.GetValue(t) as System.Collections.IEnumerable;
                                var harvested = await DownloadFromEnumerable(result, item, poolDir, needed - added.Count, cfg, ct, added).ConfigureAwait(false);
                                if (harvested > 0) break;
                            }
                        }
                        catch (Exception ex)
                        {
                            _log.LogDebug(ex, "PosterRotator: provider {Provider} GetImages failed for \"{Item}\"", pType.Name, item.Name);
                        }
                    }
                }

                foreach (var p in providers)
                {
                    await HarvestProviderAsync(p, ImageType.Primary).ConfigureAwait(false);
                    if (added.Count >= needed) break;
                }
                if (added.Count < needed)
                {
                    foreach (var p in providers)
                    {
                        await HarvestProviderAsync(p, ImageType.Thumb).ConfigureAwait(false);
                        if (added.Count >= needed) break;
                    }
                }
                if (added.Count < needed)
                {
                    foreach (var p in providers)
                    {
                        await HarvestProviderAsync(p, ImageType.Backdrop).ConfigureAwait(false);
                        if (added.Count >= needed) break;
                    }
                }

                _log.LogInformation("PosterRotator: added {Count} images via providers for \"{Item}\"", added.Count, item.Name);
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "PosterRotator: reflection-based provider top-up failed for {Name}", item.Name);
            }

            return added;

            async Task<int> DownloadFromEnumerable(
                System.Collections.IEnumerable? result,
                BaseItem movie2,
                string poolDir2,
                int toTake,
                Configuration cfg2,
                CancellationToken ct2,
                List<string> added2)
            {
                if (result == null || toTake <= 0) return 0;
                var urls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var count = 0;

                foreach (var info in result.Cast<object>())
                {
                    if (count >= toTake) break;

                    var t = info.GetType();
                    // prefer portrait images when dimension info available
                    if (IsLandscapeFromInfo(info)) continue;

                    var url = t.GetProperty("Url")?.GetValue(info) as string;
                    var mime = t.GetProperty("MimeType")?.GetValue(info) as string;

                    if (string.IsNullOrWhiteSpace(url) || !urls.Add(url)) continue;

                    var ext = GuessExt(mime) ?? GuessExtFromUrl(url) ?? ".jpg";
                    var name = $"pool_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}{ext}";
                    var full = Path.Combine(poolDir2, name);

                    try
                    {
                        using var resp = await _http.GetAsync(url, ct2).ConfigureAwait(false);
                        resp.EnsureSuccessStatusCode();
                        await using var s = await resp.Content.ReadAsStreamAsync(ct2).ConfigureAwait(false);
                        await using var f = File.Create(full);
                        await s.CopyToAsync(f, ct2).ConfigureAwait(false);

                        added2.Add(full);
                        count++;
                    }
                    catch
                    {
                        // continue
                    }
                }
                return count;
            }
        }

        // ---- helpers --------------------------------------------------------

        private static string ResolveItemDirectory(BaseItem item)
        {
            try
            {
                if (string.IsNullOrEmpty(item.Path))
                    return string.Empty;

                return Directory.Exists(item.Path)
                    ? item.Path
                    : (Path.GetDirectoryName(item.Path) ?? string.Empty);
            }
            catch
            {
                return string.Empty;
            }
        }

        // New helpers for mixed-folder handling

    private static string GetItemDir(BaseItem item) => ResolveItemDirectory(item);

        private static bool IsMixedFolder(BaseItem item, IDictionary<string,int> dirCounts)
        {
            var dir = Path.GetDirectoryName(item.Path ?? string.Empty) ?? string.Empty;
            return !string.IsNullOrEmpty(dir)
                && dirCounts.TryGetValue(dir, out var n)
                && n > 1;
        }

        private static string GetPerItemPoolDir(string itemDir, BaseItem item)
        {
            // Previously we used a per-item GUID subfolder under .poster_pool for mixed folders
            // to avoid collisions when multiple items shared the same parent folder. To keep
            // path structure consistent between Movies and Series, return the shared
            // `.poster_pool` directory directly (no per-item GUID folder). The caller
            // ensures the directory exists.
            return Path.Combine(itemDir, ".poster_pool");
        }

        private static string GetPreferredPerItemPosterPath(BaseItem item, string itemDir, string preferredExt)
        {
            var src = item.Path ?? "poster";
            var baseName = Path.GetFileNameWithoutExtension(src) ?? "poster";
            var ext = string.IsNullOrWhiteSpace(preferredExt) ? ".jpg" : preferredExt.ToLowerInvariant();

            // Prefer existing per-item poster if present (any ext), else choose a good conventional name.
            foreach (var stem in new[] { $"{baseName}-poster", baseName })
            {
                var existing = Directory.GetFiles(itemDir, stem + ".*")
                    .FirstOrDefault(f =>
                        f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                        f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                        f.EndsWith(".webp", StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrEmpty(existing))
                    return existing;
            }

            return Path.Combine(itemDir, $"{baseName}-poster{ext}");
        }

        private static IEnumerable<string> GetPoolPatterns(Configuration cfg)
        {
            var patterns = new List<string>();
            if (cfg.ExtraPosterPatterns != null)
                patterns.AddRange(cfg.ExtraPosterPatterns);

            patterns.AddRange(new[]
            {
                "*.jpg","*.jpeg","*.png","*.webp","*.gif",
                "poster*.jpg","poster*.jpeg","poster*.png","poster*.webp","poster*.gif",
                "*-poster*.jpg","*-poster*.jpeg","*-poster*.png","*-poster*.webp","*-poster*.gif" 
            });

            return patterns.Distinct(StringComparer.OrdinalIgnoreCase);
        }

        private static string? TryCopyCurrentPrimaryToPool(BaseItem item, string poolDir, bool mixedFolder = false)
        {
            try
            {
                var primary = item.GetImagePath(ImageType.Primary);
                if (!string.IsNullOrEmpty(primary) && File.Exists(primary))
                {
                    var name = "pool_currentprimary" + Path.GetExtension(primary);
                    var dest = Path.Combine(poolDir, name);
                    File.Copy(primary, dest, overwrite: true);
                    return dest;
                }

                // In mixed folders, also look for an existing per-movie poster beside the file.
                if (mixedFolder && !string.IsNullOrEmpty(item.Path))
                {
                    var dir = Path.GetDirectoryName(item.Path)!;
                    var baseName = Path.GetFileNameWithoutExtension(item.Path)!;

                    var candidates = Directory.GetFiles(dir, $"{baseName}-poster.*")
                        .Concat(Directory.GetFiles(dir, $"{baseName}.*"))
                        .Where(f => f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                                 || f.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                                 || f.EndsWith(".webp", StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    var existing = candidates.FirstOrDefault();
                    if (!string.IsNullOrEmpty(existing))
                    {
                        var name = "pool_currentprimary" + Path.GetExtension(existing);
                        var dest = Path.Combine(poolDir, name);
                        File.Copy(existing, dest, overwrite: true);
                        return dest;
                    }
                }
            }
            catch { /* ignore */ }

            return null;
        }

        // Skip pool_currentprimary.* when alternatives exist; on first rotation start at a non-current image
        private static string PickNextFor(
            List<string> files,
            BaseItem item,
            Configuration cfg,
            string poolDir,
            RotationState state)
        {
            // deterministic order: push pool_currentprimary.* to the end, then sort by filename
            var reordered = files
                .OrderBy(f => Path.GetFileName(f).StartsWith("pool_currentprimary", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                .ThenBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                .ToList();

            var key = item.Id.ToString();
            int idx;

            if (cfg.SequentialRotation)
            {
                // First time: if we have >1 image, skip the snapshot
                if (!state.LastIndexByItem.ContainsKey(key) && reordered.Count > 1)
                {
                    idx = 1;                        // use first non-snapshot
                    state.LastIndexByItem[key] = 2; // next time continue with index 2
                }
                else
                {
                    var last = state.LastIndexByItem.TryGetValue(key, out var v) ? v : 0;
                    idx = last % reordered.Count;
                    state.LastIndexByItem[key] = last + 1; // advance for next run
                }
            }
            else
            {
                // Random: prefer non-snapshot when possible
                if (reordered.Count > 1)
                {
                    var nonCurrent = reordered.Where(f =>
                        !Path.GetFileName(f).StartsWith("pool_currentprimary", StringComparison.OrdinalIgnoreCase)).ToList();
                    if (nonCurrent.Count > 0)
                    {
                        var pick = nonCurrent[Random.Shared.Next(nonCurrent.Count)];
                        idx = reordered.IndexOf(pick);
                    }
                    else
                    {
                        idx = Random.Shared.Next(reordered.Count);
                    }
                }
                else
                {
                    idx = 0;
                }

                // (We don’t touch LastIndexByItem for random mode.)
            }

            return reordered[idx];
        }

        private static void SafeOverwrite(string src, string dst)
        {
            try
            {
                if (File.Exists(dst))
                {
                    var attrs = File.GetAttributes(dst);
                    if ((attrs & FileAttributes.ReadOnly) != 0)
                        File.SetAttributes(dst, attrs & ~FileAttributes.ReadOnly);
                }
                File.Copy(src, dst, overwrite: true);
                try { File.SetLastWriteTimeUtc(dst, DateTime.UtcNow); } catch { }
            }
            catch
            {
                // Swallow; caller logs the intent already.
            }
        }

        private sealed class RotationState
        {
            public Dictionary<string, int> LastIndexByItem { get; set; } = new();
            public Dictionary<string, long> LastRotatedUtcByItem { get; set; } = new();
        }

        private static RotationState LoadState(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    var json = File.ReadAllText(path);
                    return System.Text.Json.JsonSerializer.Deserialize<RotationState>(json) ?? new RotationState();
                }
            }
            catch { /* ignore */ }

            return new RotationState();
        }

        private static void SaveState(string path, RotationState state)
        {
            try
            {
                var json = System.Text.Json.JsonSerializer.Serialize(state);
                File.WriteAllText(path, json);
            }
            catch { /* ignore */ }
        }

        private static string? GuessExt(string? mime) =>
            mime switch
            {
                "image/png" => ".png",
                "image/webp" => ".webp",
                "image/jpeg" => ".jpg",
                "image/gif"  => ".gif",  
                _ => null
            };

        private static string? GuessExtFromUrl(string url)
        {
            try
            {
                var ext = Path.GetExtension(new Uri(url).AbsolutePath);
                if (string.IsNullOrEmpty(ext)) return null;
                if (ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)) return ".jpg";
                return ext.StartsWith('.') ? ext.ToLowerInvariant() : "." + ext.ToLowerInvariant();
            }
            catch { return null; }
        }

        // Score provider name higher if it matches preferred providers (TheTVDB preferred)
        private static int PreferredProviderScore(string providerName)
        {
            if (string.IsNullOrEmpty(providerName)) return 0;
            var n = providerName.ToLowerInvariant();
            if (n.Contains("tvdb") || n.Contains("thetvdb")) return 100;
            if (n.Contains("tmdb")) return 50;
            if (n.Contains("fanart")) return 40;
            return 0;
        }

        // Return true if the info object contains width/height and indicates landscape orientation
        private static bool IsLandscapeFromInfo(object? info)
        {
            try
            {
                if (info == null) return false;
                var t = info.GetType();

                static int? GetInt(object obj, Type t, string name)
                {
                    try
                    {
                        var p = t.GetProperty(name);
                        if (p == null) return null;
                        var v = p.GetValue(obj);
                        if (v == null) return null;
                        return Convert.ToInt32(v);
                    }
                    catch { return null; }
                }

                var w = GetInt(info, t, "Width") ?? GetInt(info, t, "PixelWidth") ?? GetInt(info, t, "ThumbWidth");
                var h = GetInt(info, t, "Height") ?? GetInt(info, t, "PixelHeight") ?? GetInt(info, t, "ThumbHeight");
                if (w.HasValue && h.HasValue)
                    return w.Value > h.Value;
            }
            catch { }
            return false;
        }

        // ---- library root helpers (nudge once per root) ----------------------

        private Dictionary<string, List<string>> GetLibraryRootPaths()
        {
            try
            {
                var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

                // Reflect GetVirtualFolders() to read Names and Locations/Paths
                var lmType = _library.GetType();
                var getVf = lmType.GetMethod("GetVirtualFolders");
                if (getVf != null)
                {
                    var vfResult = getVf.Invoke(_library, null) as System.Collections.IEnumerable;
                    if (vfResult != null)
                    {
                        foreach (var vf in vfResult)
                        {
                            try
                            {
                                var name = vf.GetType().GetProperty("Name")?.GetValue(vf) as string ?? "";
                                var locProp = vf.GetType().GetProperty("Locations") ?? vf.GetType().GetProperty("Paths");
                                var locVal = locProp?.GetValue(vf) as System.Collections.IEnumerable;
                                if (!map.ContainsKey(name)) map[name] = new List<string>();
                                if (locVal != null)
                                {
                                    foreach (var p in locVal)
                                    {
                                        if (p is string s && !string.IsNullOrWhiteSpace(s))
                                            map[name].Add(s);
                                    }
                                }
                            }
                            catch { }
                        }
                    }
                }

                // dedupe each list
                foreach (var k in map.Keys.ToList())
                {
                    map[k] = map[k].Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                }

                return map;
            }
            catch
            {
                return new Dictionary<string, List<string>>();
            }
        }

        

        private void NudgeLibraryRoot(string rootPath, bool triggerScan)
        {
            try
            {
                _log.LogDebug("PosterRotator: notification à Jellyfin pour {Root}", rootPath);

                // Créer/modifier un fichier .touch pour signaler un changement
                var touch = Path.Combine(rootPath, ".posterrotator.touch");

                if (!File.Exists(touch))
                {
                    File.WriteAllText(touch, "touch");
                }
                else
                {
                    // Modifier la date d'accès pour signaler une modification
                    File.SetLastWriteTimeUtc(touch, DateTime.UtcNow);
                }

                // Aussi modifier le répertoire lui-même
                try
                {
                    Directory.SetLastWriteTimeUtc(rootPath, DateTime.UtcNow);
                }
                catch { }

                _log.LogInformation("PosterRotator: Jellyfin notifié du changement");
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "PosterRotator: impossible de notifier Jellyfin");
            }

                // Final best-effort: try to find a scheduled task manager and trigger a Library Scan task for this root.
                try
                {
                    if (_services != null && triggerScan)
                    {
                        var spType = _services.GetType();
                        var getSvc = spType.GetMethod("GetService");
                        if (getSvc != null)
                        {
                            // Search for types that look like ScheduledTaskManager / TaskManager / IScheduledTaskManager
                            var candidateTypes = spType.Assembly.GetTypes()
                                .Where(t => t.Name.IndexOf("TaskManager", StringComparison.OrdinalIgnoreCase) >= 0
                                         || t.Name.IndexOf("ScheduledTask", StringComparison.OrdinalIgnoreCase) >= 0)
                                .ToList();

                            foreach (var t in candidateTypes)
                            {
                                try
                                {
                                    var svc = getSvc.Invoke(_services, new object[] { t });
                                    if (svc == null) continue;

                                    // Look for methods to Enqueue/Run tasks
                                    var methods = t.GetMethods(BindingFlags.Public | BindingFlags.Instance);
                                    // Try to find a method that accepts a task id or name
                                    var runMethod = methods.FirstOrDefault(m => m.Name.IndexOf("Run", StringComparison.OrdinalIgnoreCase) >= 0 && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(string));
                                    if (runMethod != null)
                                    {
                                        // Many servers expose named scheduled tasks; try a few known names
                                        var candidates = new[] { "LibraryScan", "ScanLibrary", "Scan", "Library Scanner", "Scan and Analyze" };
                                        foreach (var name in candidates)
                                        {
                                            try
                                            {
                                                runMethod.Invoke(svc, new object[] { name });
                                                _log.LogInformation("PosterRotator: triggered scheduled task '{Name}' via {Svc}.{Method}", name, t.Name, runMethod.Name);
                                                return;
                                            }
                                            catch { }
                                        }
                                    }

                                    // Try other overloads: e.g., EnqueueTask(TaskDefinition) or Enqueue(Task)
                                    var enqueue = methods.FirstOrDefault(m => m.Name.IndexOf("Enqueue", StringComparison.OrdinalIgnoreCase) >= 0 || m.Name.IndexOf("Add", StringComparison.OrdinalIgnoreCase) >= 0);
                                    if (enqueue != null)
                                    {
                                        try
                                        {
                                            // Attempt to find a task definition type in the same assembly that might represent a LibraryScan task
                                            var taskDefType = spType.Assembly.GetTypes().FirstOrDefault(tt => tt.Name.IndexOf("Library", StringComparison.OrdinalIgnoreCase) >= 0 && tt.Name.IndexOf("Scan", StringComparison.OrdinalIgnoreCase) >= 0);
                                            if (taskDefType != null)
                                            {
                                                // Try to create one with a path property if available
                                                var td = Activator.CreateInstance(taskDefType);
                                                if (td != null)
                                                {
                                                    var pathProp = taskDefType.GetProperty("Path") ?? taskDefType.GetProperty("LibraryPath") ?? taskDefType.GetProperty("RootPath");
                                                    if (pathProp != null)
                                                    {
                                                        try { pathProp.SetValue(td, rootPath); } catch { }
                                                    }
                                                    try
                                                    {
                                                        enqueue.Invoke(svc, new object[] { td });
                                                        _log.LogInformation("PosterRotator: enqueued a library scan task via {Svc}.{Method}", t.Name, enqueue.Name);
                                                        return;
                                                    }
                                                    catch { }
                                                }
                                            }
                                        }
                                        catch { }
                                    }
                                }
                                catch { }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _log.LogDebug(ex, "PosterRotator: scheduled-task trigger attempts failed for {Root}", rootPath);
                }
        }
    }
}
