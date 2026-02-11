using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using Jellyfin.Data.Enums;
using Jellyfin.Plugin.PosterRotator.Helpers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PosterRotator
{
    public class PosterRotatorService
    {
        private readonly ILibraryManager _library;
        private readonly IServiceProvider _services;
        private readonly IHttpClientFactory _httpFactory;
        private readonly ILogger<PosterRotatorService> _log;

        public PosterRotatorService(
            ILibraryManager library,
            IServiceProvider services,
            IHttpClientFactory httpFactory,
            ILogger<PosterRotatorService> log)
        {
            _library = library;
            _services = services;
            _httpFactory = httpFactory;
            _log = log;
        }

        // Providers cached for the duration of a single run (perf #7)
        private IReadOnlyList<IRemoteImageProvider>? _cachedProviders;
        private readonly object _providersLock = new();

        public async Task RunAsync(Configuration cfg, IProgress<double>? progress, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            int rotatedCount = 0, skippedCount = 0, errorCount = 0, topUpCount = 0;

            // Resolve providers once for the entire run (thread-safe Q9)
            var providers = ResolveImageProviders().ToList();
            lock (_providersLock) { _cachedProviders = providers; }

            try
            {
                var kinds = new List<BaseItemKind>
                {
                    BaseItemKind.Movie,
                    BaseItemKind.Series,
                    BaseItemKind.BoxSet
                };

                if (cfg.EnableSeasonPosters) kinds.Add(BaseItemKind.Season);
                if (cfg.EnableEpisodePosters) kinds.Add(BaseItemKind.Episode);

                var q = new InternalItemsQuery
                {
                    IncludeItemTypes = kinds.Distinct().ToArray(),
                    Recursive = true
                };

                var items = _library.GetItemList(q).ToList();

                if (items.Count == 0)
                {
                    _log.LogWarning("PosterRotator: no items returned by library manager; aborting run.");
                    return;
                }

                var dirCounts = items
                    .Select(m => Path.GetDirectoryName(m.Path ?? string.Empty) ?? string.Empty)
                    .GroupBy(d => d, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

                var total = items.Count;
                var done = 0;

                var libraryMap = GetLibraryRootPaths();
                if (_log.IsEnabled(LogLevel.Debug))
                    _log.LogDebug("PosterRotator: discovered library map: {Map}", string.Join("; ", libraryMap.Select(kv => kv.Key + ":" + string.Join(",", kv.Value))));

                var allLibraryRoots = libraryMap.SelectMany(kv => kv.Value).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var rootsToNudge = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                var configuredNames = new List<string>();
                if (cfg.LibraryRules is { Count: > 0 })
                    configuredNames.AddRange(cfg.LibraryRules.Where(r => r.Enabled).Select(r => r.Name));
                else if (cfg.Libraries is { Count: > 0 })
                    configuredNames.AddRange(cfg.Libraries);

                var selection = ResolveSelectedRoots(cfg, configuredNames, libraryMap, allLibraryRoots);
                var selectedRoots = selection.Paths;
                var selectedLibraryNames = selection.LibraryNames;
                var hasSelection = selectedRoots.Count > 0 || selectedLibraryNames.Count > 0;

                foreach (var item in items)
                {
                    ct.ThrowIfCancellationRequested();
                    if (hasSelection)
                    {
                        var path = item.Path ?? string.Empty;
                        var matchesPath = selectedRoots.Any(r => LooksLikePath(r) && path.StartsWith(r, StringComparison.OrdinalIgnoreCase));
                        var matchesLibraryName = IsUnderSelectedLibraryNames(path, selectedLibraryNames, libraryMap);

                        if (!matchesPath && !matchesLibraryName)
                        {
                            skippedCount++;
                            progress?.Report(++done * 100.0 / Math.Max(1, total));
                            continue;
                        }
                    }
                    try
                    {
                        var (rotated, topped) = await ProcessItemAsync(item, cfg, ct, dirCounts).ConfigureAwait(false);
                        if (rotated)
                        {
                            rotatedCount++;
                            var path = item.Path ?? string.Empty;
                            var root = allLibraryRoots.FirstOrDefault(r => LooksLikePath(r) && path.StartsWith(r, StringComparison.OrdinalIgnoreCase));
                            if (!string.IsNullOrEmpty(root))
                                rootsToNudge.Add(root);
                        }
                        topUpCount += topped;
                    }
                    catch (Exception ex)
                    {
                        errorCount++;
                        _log.LogWarning(ex, "PosterRotator: error processing \"{Name}\" ({Path})", item.Name, item.Path);
                    }

                    progress?.Report(++done * 100.0 / Math.Max(1, total));
                }

                foreach (var root in rootsToNudge)
                    NudgeLibraryRoot(root);

                // Run summary (#15)
                sw.Stop();
                _log.LogInformation(
                    "PosterRotator: run complete — {Rotated}/{Total} rotated, {TopUp} top-ups, {Skipped} skipped, {Errors} errors, took {Elapsed:0.0}s",
                    rotatedCount, total, topUpCount, skippedCount, errorCount, sw.Elapsed.TotalSeconds);
            }
            finally
            {
                lock (_providersLock) { _cachedProviders = null; }
            }
        }

        private SelectedRoots ResolveSelectedRoots(
            Configuration cfg,
            List<string> configuredNames,
            Dictionary<string, List<string>> libraryMap,
            List<string> allLibraryRoots)
        {
            var result = new SelectedRoots();

            try
            {
                if (cfg.ManualLibraryRoots is { Count: > 0 })
                {
                    var manual = cfg.ManualLibraryRoots
                        .Where(s => !string.IsNullOrWhiteSpace(s))
                        .Select(s => s.Trim())
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    if (manual.Count > 0)
                    {
                        foreach (var entry in manual)
                        {
                            if (LooksLikePath(entry))
                            {
                                result.Paths.Add(entry);
                                continue;
                            }

                            if (libraryMap.TryGetValue(entry, out var paths) && paths is { Count: > 0 })
                            {
                                result.Paths.AddRange(paths);
                                result.LibraryNames.Add(entry);
                            }
                            else
                            {
                                _log.LogWarning("PosterRotator: manual entry '{Entry}' does not match any known library", entry);
                            }
                        }

                        DeduplicatePaths(result.Paths);
                        _log.LogInformation("PosterRotator: using ManualLibraryRoots resolved to: {Roots}", string.Join(",", result.Paths));
                        return result;
                    }
                }
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "PosterRotator: error resolving ManualLibraryRoots");
            }

            if (configuredNames.Count > 0)
            {
                foreach (var name in configuredNames)
                {
                    if (libraryMap.TryGetValue(name, out var paths) && paths is { Count: > 0 })
                    {
                        result.Paths.AddRange(paths);
                        result.LibraryNames.Add(name);
                    }
                    else
                    {
                        _log.LogWarning("PosterRotator: configured library '{Name}' not found among current libraries", name);
                    }
                }

                DeduplicatePaths(result.Paths);

                if (result.Paths.Count > 0)
                {
                    _log.LogInformation("PosterRotator: configured libraries: {Cfg}", string.Join(",", configuredNames));
                    _log.LogInformation("PosterRotator: selected roots to process: {Roots}", string.Join(",", result.Paths));
                }

                return result;
            }

            if (allLibraryRoots.Count > 0)
            {
                result.Paths.AddRange(allLibraryRoots.Distinct(StringComparer.OrdinalIgnoreCase));
                _log.LogInformation("PosterRotator: no libraries configured -> defaulting to all library roots: {Roots}", string.Join(",", result.Paths));
            }

            return result;
        }

        private static void DeduplicatePaths(List<string> paths)
        {
            if (paths.Count <= 1) return;
            var unique = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            paths.Clear();
            paths.AddRange(unique);
        }

        private static bool IsUnderSelectedLibraryNames(
            string path,
            HashSet<string> allowedLibraryNames,
            Dictionary<string, List<string>> libraryMap)
        {
            if (string.IsNullOrEmpty(path) || allowedLibraryNames.Count == 0)
                return false;

            foreach (var name in allowedLibraryNames)
            {
                if (!libraryMap.TryGetValue(name, out var roots) || roots.Count == 0)
                    continue;

                foreach (var root in roots)
                {
                    if (LooksLikePath(root) && path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }

            return false;
        }

        private sealed class SelectedRoots
        {
            public List<string> Paths { get; } = new();
            public HashSet<string> LibraryNames { get; } = new(StringComparer.OrdinalIgnoreCase);
        }

        private static bool LooksLikePath(string entry) =>
            entry.IndexOf(':') >= 0 || entry.IndexOf('\\') >= 0 || entry.IndexOf('/') >= 0;

        /// <summary>
        /// Process a single media item: top-up pool + rotate poster.
        /// Returns (didRotate, topUpCount).
        /// </summary>
        private async Task<(bool Rotated, int TopUps)> ProcessItemAsync(
            BaseItem item, Configuration cfg, CancellationToken ct, IDictionary<string,int> dirCounts)
        {
            var itemDir = PluginHelpers.GetItemDirectory(item.Path) ?? string.Empty;
            if (string.IsNullOrEmpty(itemDir) || !Directory.Exists(itemDir))
                return (false, 0);

            var mixedFolder = IsMixedFolder(item, dirCounts);

            var poolDir = mixedFolder
                ? GetPerItemPoolDir(itemDir)
                : Path.Combine(itemDir, ".poster_pool");
            Directory.CreateDirectory(poolDir);

            // Load existing images in the pool.
            var local = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pat in GetPoolPatterns(cfg))
                foreach (var f in Directory.GetFiles(poolDir, pat))
                    local.Add(f);

            var lockFile = Path.Combine(poolDir, "pool.lock");
            var poolIsLocked = File.Exists(lockFile);

            var statePath = Path.Combine(poolDir, "rotation_state.json");
            var state = PluginHelpers.LoadRotationState(statePath);
            var key = item.Id.ToString();
            var now = DateTimeOffset.UtcNow;

            bool haveLast = state.LastRotatedUtcByItem.TryGetValue(key, out var lastEpoch);
            var elapsed = haveLast ? (now - DateTimeOffset.FromUnixTimeSeconds(lastEpoch)) : TimeSpan.MaxValue;
            var minHours = Math.Max(1, cfg.MinHoursBetweenSwitches);

            bool allowTopUp = !haveLast || elapsed.TotalHours >= minHours || local.Count == 0;

            if (_log.IsEnabled(LogLevel.Debug))
                _log.LogDebug("PosterRotator: \"{Item}\" pool has {Count}/{Target}. Locked:{Locked}. AllowTopUp:{Allow} (elapsed {H:0.0}h, min {Min}h)",
                    item.Name, local.Count, cfg.PoolSize, poolIsLocked, allowTopUp, haveLast ? elapsed.TotalHours : -1, minHours);

            int topUpCount = 0;

            // Top up the pool when size is low and cooldown allows provider calls.
            if (!poolIsLocked && local.Count < cfg.PoolSize && allowTopUp)
            {
                var need = cfg.PoolSize - local.Count;
                var added = await TryTopUpFromProvidersAsync(item, poolDir, need, cfg, ct).ConfigureAwait(false);
                topUpCount = added.Count;
                foreach (var f in added) local.Add(f);

                if (!poolIsLocked && cfg.LockImagesAfterFill && local.Count >= cfg.PoolSize)
                {
                    try { File.WriteAllText(lockFile, "locked"); } catch { }
                    poolIsLocked = true;
                    _log.LogInformation("PosterRotator: locked pool for \"{Item}\" at size {Size}.", item.Name, local.Count);
                }
            }
            else if (!poolIsLocked && local.Count < cfg.PoolSize && !allowTopUp)
            {
                if (_log.IsEnabled(LogLevel.Debug))
                    _log.LogDebug("PosterRotator: skipping top-up for \"{Item}\" due to cooldown (elapsed {H:0.0}h < {Min}h)", item.Name, elapsed.TotalHours, minHours);
            }
            else if (poolIsLocked && !cfg.LockImagesAfterFill)
            {
                try { File.Delete(lockFile); } catch { }
                poolIsLocked = false;
                _log.LogInformation("PosterRotator: unlocked pool for \"{Item}\" (config changed).", item.Name);
            }

            // Ensure we at least keep the current poster as a fallback option.
            if (local.Count == 0)
            {
                var primaryPath = TryCopyCurrentPrimaryToPool(item, poolDir, mixedFolder);
                if (primaryPath != null) local.Add(primaryPath);
            }

            if (local.Count == 0)
                return (false, topUpCount);

            // Choose the next poster candidate and prepare the destination.
            var files = local.ToList();
            var chosen = PickNextFor(files, item, cfg, state);

            var currentPrimary = item.GetImagePath(ImageType.Primary);
            var chosenExt = Path.GetExtension(chosen);
            if (string.IsNullOrEmpty(chosenExt)) chosenExt = ".jpg";

            string? destinationPath;
            if (!string.IsNullOrEmpty(currentPrimary))
                destinationPath = currentPrimary;
            else if (mixedFolder)
                destinationPath = GetPreferredPerItemPosterPath(item, itemDir, chosenExt);
            else
                destinationPath = Path.Combine(itemDir, "poster.jpg");

            if (string.IsNullOrEmpty(destinationPath))
                return (false, topUpCount);

            var destDir = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);

            // Bug #2 fix: SafeOverwrite now returns false on failure
            if (!SafeOverwrite(chosen, destinationPath))
            {
                _log.LogWarning("PosterRotator: SafeOverwrite failed for \"{Item}\"", item.Name);
                return (false, topUpCount);
            }

            // Notify Jellyfin
            try
            {
                await item.UpdateToRepositoryAsync(ItemUpdateType.ImageUpdate, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "PosterRotator: unable to notify Jellyfin for '{Name}'", item.Name);
            }

            // Touch parent directory so filesystem watchers detect the change (bug #1 fix: single touch)
            try
            {
                if (!string.IsNullOrEmpty(itemDir) && Directory.Exists(itemDir))
                    Directory.SetLastWriteTimeUtc(itemDir, DateTime.UtcNow);
            }
            catch { }

            // Bug #3 fix: only persist state when we actually rotated
            state.LastRotatedUtcByItem[key] = now.ToUnixTimeSeconds();
            PluginHelpers.SaveRotationState(statePath, state);

            _log.LogInformation("PosterRotator: rotated \"{Item}\" → {Poster}",
                item.Name, Path.GetFileName(chosen));

            return (true, topUpCount);
        }

        // --- Provider top-up via DI (single unified method) ---
        private async Task<List<string>> TryTopUpFromProvidersAsync(
            BaseItem item, string poolDir, int needed, Configuration cfg, CancellationToken ct)
        {
            var added = new List<string>();
            try
            {
                IReadOnlyList<IRemoteImageProvider> provList;
                lock (_providersLock) { provList = _cachedProviders ?? ResolveImageProviders().ToList(); }

                if (provList.Count == 0)
                {
                    _log.LogDebug("PosterRotator: no image providers found for \"{Item}\"", item.Name);
                    return added;
                }

                _log.LogDebug("PosterRotator: provider top-up target {Needed} for \"{Item}\" (providers: {Count}: {Names})",
                    needed, item.Name, provList.Count, string.Join(", ", provList.Select(p => p.GetType().Name)));

                // Prefer certain providers (e.g. TheTVDB) by moving them to the front
                provList = provList.OrderByDescending(p => PreferredProviderScore(p.GetType().Name)).ToList();

                foreach (var provider in provList)
                {
                    if (added.Count >= needed) break;

                    try
                    {
                        bool supports = true;
                        IEnumerable<ImageType>? supportedTypes = null;

                        try { supports = provider.Supports(item); } catch { }
                        try { supportedTypes = provider.GetSupportedImages(item); } catch { }

                        if (!supports)
                        {
                            _log.LogDebug("PosterRotator: provider {Prov} does not support \"{Item}\"", provider.GetType().Name, item.Name);
                            continue;
                        }

                        var prefersPrimary = supportedTypes?.Contains(ImageType.Primary) == true;

                        var images = await provider.GetImages(item, ct).ConfigureAwait(false);
                        await Harvest(images, preferPrimary: prefersPrimary).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _log.LogDebug(ex, "PosterRotator: provider {Provider} failed for \"{Item}\"",
                            provider.GetType().Name, item.Name);
                    }
                }

                _log.LogInformation("PosterRotator: providers added {Count} image(s) for \"{Item}\"", added.Count, item.Name);
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "PosterRotator: provider top-up failed for {Name}", item.Name);
            }

            return added;

            // order + download a batch with language filtering
            async Task<bool> Harvest(IEnumerable<RemoteImageInfo>? images, bool preferPrimary)
            {
                if (images == null) return false;

                var imageList = images.ToList();
                if (imageList.Count == 0) return false;

                var gotAny = false;

                // Apply language filtering if enabled
                if (cfg.EnableLanguageFilter)
                {
                    var prefLang = cfg.PreferredLanguage?.ToLowerInvariant() ?? "fr";
                    var maxPrefLang = cfg.MaxPreferredLanguageImages;
                    var includeUnknown = cfg.IncludeUnknownLanguage;

                    string fallbackLang;
                    if (cfg.UseOriginalLanguageAsFallback)
                    {
                        fallbackLang = GetOriginalLanguage(item)?.ToLowerInvariant() ?? "";
                        _log.LogDebug("PosterRotator: Using original language '{Lang}' for {Item}", fallbackLang, item.Name);
                    }
                    else
                    {
                        fallbackLang = cfg.FallbackLanguage?.ToLowerInvariant() ?? "";
                    }

                    var prefLangInPool = CountLanguageImagesInPool(poolDir, prefLang);
                    var remainingPrefSlots = Math.Max(0, maxPrefLang - prefLangInPool);

                    _log.LogDebug("PosterRotator: Language filter - preferred={PrefLang}, fallback={FallLang}, max={Max}, inPool={InPool}, remaining={Rem}",
                        prefLang, fallbackLang, maxPrefLang, prefLangInPool, remainingPrefSlots);

                    var prefLangImages = imageList
                        .Where(i => i.Type == ImageType.Primary && 
                                   !string.IsNullOrEmpty(i.Language) && 
                                   i.Language.Equals(prefLang, StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    var fallbackImages = imageList
                        .Where(i => i.Type == ImageType.Primary && 
                                   (string.IsNullOrEmpty(fallbackLang) || 
                                    (!string.IsNullOrEmpty(i.Language) && i.Language.Equals(fallbackLang, StringComparison.OrdinalIgnoreCase)) ||
                                    (includeUnknown && string.IsNullOrEmpty(i.Language))))
                        .Where(i => !prefLangImages.Contains(i))
                        .ToList();

                    _log.LogDebug("PosterRotator: Found {PrefCount} {PrefLang} images, {FallCount} {FallLang} images",
                        prefLangImages.Count, prefLang, fallbackImages.Count, fallbackLang);

                    foreach (var info in prefLangImages.Take(remainingPrefSlots))
                    {
                        if (added.Count >= needed) break;
                        await TryDownloadRemote(info, item, poolDir, ct, added, prefLang).ConfigureAwait(false);
                        gotAny = true;
                    }

                    foreach (var info in fallbackImages)
                    {
                        if (added.Count >= needed) break;
                        await TryDownloadRemote(info, item, poolDir, ct, added, info.Language ?? "unknown").ConfigureAwait(false);
                        gotAny = true;
                    }
                }
                else
                {
                    // Original behavior without language filtering
                    var ordered = (preferPrimary
                            ? imageList.OrderByDescending(i => i.Type == ImageType.Primary).ThenBy(i => i.ProviderName)
                            : imageList.OrderBy(i => i.ProviderName))
                        .ToList();

                    foreach (var info in ordered.Where(i => i.Type == ImageType.Primary))
                    {
                        if (added.Count >= needed) break;
                        await TryDownloadRemote(info, item, poolDir, ct, added, null).ConfigureAwait(false);
                        gotAny = true;
                    }

                    if (added.Count < needed)
                    {
                        foreach (var info in ordered.Where(i => i.Type == ImageType.Thumb || i.Type == ImageType.Backdrop))
                        {
                            if (added.Count >= needed) break;
                            await TryDownloadRemote(info, item, poolDir, ct, added, null).ConfigureAwait(false);
                            gotAny = true;
                        }
                    }
                }

                return gotAny;
            }

            int CountLanguageImagesInPool(string dir, string lang)
            {
                if (!Directory.Exists(dir)) return 0;
                var metaPath = Path.Combine(dir, "pool_languages.json");
                return PluginHelpers.CountInJsonMap(metaPath, kv => kv.Value.Equals(lang, StringComparison.OrdinalIgnoreCase));
            }

            async Task TryDownloadRemote(RemoteImageInfo info,
                                        BaseItem mv, string dir,
                                        CancellationToken token, List<string> bucket, string? language)
            {
                if (info == null) return;

                // Prefer portrait images: skip landscape images
                if (IsLandscape(info)) return;

                var url = info.Url;
                if (string.IsNullOrWhiteSpace(url)) return;

                var ext = PluginHelpers.GuessExtFromUrl(url) ?? ".jpg";
                var name = $"pool_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}{ext}";
                var full = Path.Combine(dir, name);

                try
                {
                    using var client = _httpFactory.CreateClient("PosterRotator");
                    using var resp = await client.GetAsync(url, token).ConfigureAwait(false);
                    resp.EnsureSuccessStatusCode();
                    await using var s = await resp.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                    await using var f = File.Create(full);
                    await s.CopyToAsync(f, token).ConfigureAwait(false);

                    bucket.Add(full);

                    // Save language metadata if available
                    var actualLang = language ?? info.Language ?? "unknown";
                    if (!string.IsNullOrEmpty(actualLang))
                    {
                        SaveLanguageMetadata(dir, name, actualLang);
                    }

                    _log.LogDebug("PosterRotator: downloaded {Name} (lang={Lang}) for {Item}", name, actualLang, mv.Name);
                }
                catch (Exception ex)
                {
                    _log.LogDebug(ex, "PosterRotator: download failed for {Url} ({Item})", url, mv.Name);
                }
            }

            // Bug #4 fix: atomic write via PluginHelpers
            void SaveLanguageMetadata(string dir, string fileName, string lang)
            {
                var metaPath = Path.Combine(dir, "pool_languages.json");
                PluginHelpers.UpdateJsonMapFile(metaPath, fileName, lang);
            }
        }

        /// <summary>
        /// Get the original language of a media item from its metadata.
        /// Uses direct property access — no reflection needed.
        /// </summary>
        private string? GetOriginalLanguage(BaseItem item)
        {
            try
            {
                // 1. Try OriginalTitle comparison to detect language from characters
                var originalTitle = item.OriginalTitle;
                var name = item.Name;

                if (!string.IsNullOrEmpty(originalTitle) && !string.IsNullOrEmpty(name))
                {
                    if (!originalTitle.Equals(name, StringComparison.OrdinalIgnoreCase))
                    {
                        return DetectLanguageFromTitle(originalTitle);
                    }
                }

                // 2. Check provider IDs for hints
                var providerIds = item.ProviderIds;
                if (providerIds != null)
                {
                    if (providerIds.ContainsKey("AniDB") || providerIds.ContainsKey("AniList"))
                        return "ja";

                    if (item.Name?.Contains("드라마") == true || item.Path?.Contains("Korean") == true)
                        return "ko";
                }

                // 3. Path-based detection
                var path = item.Path;
                if (!string.IsNullOrEmpty(path))
                {
                    var pathLower = path.ToLowerInvariant();
                    if (pathLower.Contains("/japanese/") || pathLower.Contains("\\japanese\\") || pathLower.Contains("/anime/"))
                        return "ja";
                    if (pathLower.Contains("/french/") || pathLower.Contains("\\french\\"))
                        return "fr";
                    if (pathLower.Contains("/german/") || pathLower.Contains("\\german\\"))
                        return "de";
                    if (pathLower.Contains("/spanish/") || pathLower.Contains("\\spanish\\"))
                        return "es";
                    if (pathLower.Contains("/korean/") || pathLower.Contains("\\korean\\"))
                        return "ko";
                }
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "PosterRotator: Failed to detect original language for {Item}", item.Name);
            }

            return "en";
        }

        /// <summary>
        /// Simple heuristic to detect language from title characters.
        /// </summary>
        private string? DetectLanguageFromTitle(string title)
        {
            if (string.IsNullOrEmpty(title)) return null;

            foreach (var c in title)
            {
                if (c >= 0x3040 && c <= 0x309F) return "ja"; // Hiragana
                if (c >= 0x30A0 && c <= 0x30FF) return "ja"; // Katakana
                if (c >= 0xAC00 && c <= 0xD7AF) return "ko"; // Hangul
                if (c >= 0x4E00 && c <= 0x9FFF)
                {
                    var hasKana = title.Any(ch => (ch >= 0x3040 && ch <= 0x30FF));
                    if (!hasKana) return "zh";
                }
                if (c >= 0x0400 && c <= 0x04FF) return "ru"; // Cyrillic
                if (c >= 0x0600 && c <= 0x06FF) return "ar"; // Arabic
                if (c >= 0x0590 && c <= 0x05FF) return "he"; // Hebrew
                if (c >= 0x0E00 && c <= 0x0E7F) return "th"; // Thai
            }

            return "en";
        }

        /// <summary>
        /// Resolve image providers via DI — typed IEnumerable resolution.
        /// </summary>
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

    // Helper methods

        private static string GetItemDir(BaseItem item) =>
            PluginHelpers.GetItemDirectory(item.Path) ?? string.Empty;

        private static bool IsMixedFolder(BaseItem item, IDictionary<string,int> dirCounts)
        {
            var dir = Path.GetDirectoryName(item.Path ?? string.Empty) ?? string.Empty;
            return !string.IsNullOrEmpty(dir)
                && dirCounts.TryGetValue(dir, out var n)
                && n > 1;
        }

        private static string GetPerItemPoolDir(string itemDir)
        {
            return Path.Combine(itemDir, ".poster_pool");
        }

        private static string GetPreferredPerItemPosterPath(BaseItem item, string itemDir, string preferredExt)
        {
            var src = item.Path ?? "poster";
            var baseName = Path.GetFileNameWithoutExtension(src) ?? "poster";
            var ext = string.IsNullOrWhiteSpace(preferredExt) ? ".jpg" : preferredExt.ToLowerInvariant();

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
            // R1: Pool dir only contains pool images, so simple extension patterns suffice.
            // ExtraPosterPatterns are for external dirs, not .poster_pool/
            return new[] { "*.jpg", "*.jpeg", "*.png", "*.webp", "*.gif" };
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

        private static string PickNextFor(
            List<string> files,
            BaseItem item,
            Configuration cfg,
            RotationState state)
        {
            var reordered = files
                .OrderBy(f => Path.GetFileName(f).StartsWith("pool_currentprimary", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                .ThenBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                .ToList();

            var key = item.Id.ToString();
            int idx;

            if (cfg.SequentialRotation)
            {
                if (!state.LastIndexByItem.ContainsKey(key) && reordered.Count > 1)
                {
                    idx = 1;
                    state.LastIndexByItem[key] = 2;
                }
                else
                {
                    var last = state.LastIndexByItem.TryGetValue(key, out var v) ? v : 0;
                    idx = last % reordered.Count;
                    state.LastIndexByItem[key] = last + 1;
                }
            }
            else
            {
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
            }

            return reordered[idx];
        }

        /// <summary>
        /// Bug #2 fix: SafeOverwrite now returns false on failure instead of silently swallowing.
        /// </summary>
        private bool SafeOverwrite(string src, string dst)
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
                return true;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "PosterRotator: SafeOverwrite failed: {Src} → {Dst}", src, dst);
                return false;
            }
        }

        private static int PreferredProviderScore(string providerName)
        {
            if (string.IsNullOrEmpty(providerName)) return 0;
            var n = providerName.ToLowerInvariant();
            if (n.Contains("tvdb") || n.Contains("thetvdb")) return 100;
            if (n.Contains("tmdb")) return 50;
            if (n.Contains("fanart")) return 40;
            return 0;
        }

        private static bool IsLandscape(RemoteImageInfo info)
            => info.Width > 0 && info.Height > 0 && info.Width > info.Height;

    // Library root helpers

        /// <summary>
        /// Get virtual folders — direct call to ILibraryManager.
        /// </summary>
        private Dictionary<string, List<string>> GetLibraryRootPaths()
        {
            try
            {
                var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

                var virtualFolders = _library.GetVirtualFolders();
                foreach (var vf in virtualFolders)
                {
                    var name = vf.Name ?? "";
                    if (!map.ContainsKey(name)) map[name] = new List<string>();

                    if (vf.Locations != null)
                    {
                        foreach (var loc in vf.Locations)
                        {
                            if (!string.IsNullOrWhiteSpace(loc))
                                map[name].Add(loc);
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

        /// <summary>
        /// Nudge Jellyfin to notice poster changes by touching files.
        /// </summary>
        private void NudgeLibraryRoot(string rootPath)
        {
            try
            {
                _log.LogDebug("PosterRotator: notifying Jellyfin for {Root}", rootPath);

                var touch = Path.Combine(rootPath, ".posterrotator.touch");

                if (!File.Exists(touch))
                {
                    File.WriteAllText(touch, "touch");
                }
                else
                {
                    File.SetLastWriteTimeUtc(touch, DateTime.UtcNow);
                }

                try
                {
                    Directory.SetLastWriteTimeUtc(rootPath, DateTime.UtcNow);
                }
                catch { }

                _log.LogInformation("PosterRotator: Jellyfin notified about poster updates");
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "PosterRotator: unable to notify Jellyfin");
            }
        }
    }
}
