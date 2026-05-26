using System.Text.Json;
using Jellyfin.Plugin.PosterRotator.Helpers;
using Xunit;

namespace Jellyfin.Plugin.PosterRotator.Tests;

public sealed class PoolStoreTests
{
    [Fact]
    public async Task EnsurePoolAsync_IgnoresLegacyMapsAndUsesDedicatedPoolRoot()
    {
        var root = CreateTempPluginDataFolder();
        var itemId = Guid.NewGuid();

        try
        {
            var store = new PoolStore(root);
            var poolDir = store.TryGetPoolDirectory(itemId, create: true)!;
            var imagePath = Path.Combine(poolDir, "poster.png");
            await File.WriteAllBytesAsync(imagePath, Png1x1);

            var state = new RotationState();
            state.LastIndexByItem[itemId.ToString()] = 4;
            state.LastRotatedUtcByItem[itemId.ToString()] = 1_700_000_000;
            await File.WriteAllTextAsync(Path.Combine(poolDir, "rotation_state.json"), JsonSerializer.Serialize(state));
            await File.WriteAllTextAsync(Path.Combine(poolDir, "pool_languages.json"), JsonSerializer.Serialize(new Dictionary<string, string> { ["poster.png"] = "fr" }));
            await File.WriteAllTextAsync(Path.Combine(poolDir, "pool_urls.json"), JsonSerializer.Serialize(new Dictionary<string, string> { ["poster.png"] = "https://example.invalid/poster.png" }));
            await File.WriteAllTextAsync(Path.Combine(poolDir, "pool_hashes.json"), JsonSerializer.Serialize(new Dictionary<string, ulong> { ["poster.png"] = 123 }));

            var pool = await store.EnsurePoolAsync(
                new PoolItemSnapshot(itemId, "Movie A", "Movie", "Films", "D:\\Media\\Movie A.mkv"),
                poolDir,
                CancellationToken.None);

            Assert.Equal(PoolRoot(root), store.TryGetPoolRootPath(create: false));
            Assert.EndsWith("Jellyfin.Plugin.PosterRotator.pools", PoolRoot(root));
            Assert.True(File.Exists(Path.Combine(PoolRoot(root), "index.json")));
            Assert.True(File.Exists(Path.Combine(poolDir, "pool.json")));
            Assert.True(File.Exists(Path.Combine(poolDir, "rotation_state.json")));
            Assert.True(File.Exists(Path.Combine(poolDir, "pool_languages.json")));
            Assert.True(File.Exists(Path.Combine(poolDir, "pool_urls.json")));
            Assert.True(File.Exists(Path.Combine(poolDir, "pool_hashes.json")));
            Assert.Equal(0, pool.LastIndex);
            Assert.Single(pool.Images);
            Assert.Equal("unknown", pool.Images[0].Language);
            Assert.Null(pool.Images[0].SourceUrl);
            Assert.Equal((ulong)0, pool.Images[0].Hash);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task PurgeAsync_CanDeleteOnlyOneLibrary()
    {
        var root = CreateTempPluginDataFolder();
        var filmsId = Guid.NewGuid();
        var seriesId = Guid.NewGuid();

        try
        {
            var store = new PoolStore(root);
            await CreatePool(store, root, filmsId, "Film", "Films");
            await CreatePool(store, root, seriesId, "Serie", "Series");

            var result = await store.PurgeAsync(
                new PoolPurgeRequest { Scope = "library", LibraryName = "Films" },
                _ => true,
                CancellationToken.None);

            Assert.Equal(1, result.DeletedCount);
            Assert.False(Directory.Exists(Path.Combine(PoolRoot(root), filmsId.ToString("N"))));
            Assert.True(Directory.Exists(Path.Combine(PoolRoot(root), seriesId.ToString("N"))));
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task PurgeAsync_CanDeleteOnlyOrphans()
    {
        var root = CreateTempPluginDataFolder();
        var liveId = Guid.NewGuid();
        var orphanId = Guid.NewGuid();

        try
        {
            var store = new PoolStore(root);
            await CreatePool(store, root, liveId, "Live", "Films");
            await CreatePool(store, root, orphanId, "Orphan", "Films");

            var result = await store.PurgeAsync(
                new PoolPurgeRequest { Scope = "orphans" },
                itemId => itemId == liveId,
                CancellationToken.None);

            Assert.Equal(1, result.DeletedCount);
            Assert.True(Directory.Exists(Path.Combine(PoolRoot(root), liveId.ToString("N"))));
            Assert.False(Directory.Exists(Path.Combine(PoolRoot(root), orphanId.ToString("N"))));
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task ListPoolsAsync_FiltersErrorsAndEmptyPoolsFromIndex()
    {
        var root = CreateTempPluginDataFolder();
        var okId = Guid.NewGuid();
        var emptyId = Guid.NewGuid();
        var errorId = Guid.NewGuid();

        try
        {
            var store = new PoolStore(root);
            await CreatePool(store, root, okId, "Ok", "Films");
            await CreatePool(store, root, errorId, "Broken", "Films");
            await store.RecordErrorAsync(errorId, "Download failed.", CancellationToken.None);

            var emptyDir = Path.Combine(PoolRoot(root), emptyId.ToString("N"));
            Directory.CreateDirectory(emptyDir);
            await store.EnsurePoolAsync(
                new PoolItemSnapshot(emptyId, "Empty", "Movie", "Films", null),
                emptyDir,
                CancellationToken.None);

            var errors = await store.ListPoolsAsync(new PoolListQuery { HasErrors = true }, CancellationToken.None);
            var empty = await store.ListPoolsAsync(new PoolListQuery { IsEmpty = true }, CancellationToken.None);
            var filled = await store.ListPoolsAsync(new PoolListQuery { IsEmpty = false }, CancellationToken.None);

            Assert.Single(errors.Items);
            Assert.Equal(errorId.ToString(), errors.Items[0].ItemId);
            Assert.Single(empty.Items);
            Assert.Equal(emptyId.ToString(), empty.Items[0].ItemId);
            Assert.Equal(2, filled.Total);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task ListPoolsAsync_AutoRebuildsMissingIndexWhenPoolDirectoriesExist()
    {
        var root = CreateTempPluginDataFolder();
        var itemId = Guid.NewGuid();

        try
        {
            var store = new PoolStore(root);
            await CreatePool(store, root, itemId, "Movie", "Films");
            var indexPath = Path.Combine(PoolRoot(root), "index.json");
            File.Delete(indexPath);

            var first = await store.ListPoolsAsync(new PoolListQuery(), CancellationToken.None);
            var rebuiltWriteUtc = File.GetLastWriteTimeUtc(indexPath);
            var second = await store.ListPoolsAsync(new PoolListQuery(), CancellationToken.None);

            Assert.Equal(1, first.Total);
            Assert.Equal(itemId.ToString(), first.Items.Single().ItemId);
            Assert.Equal(1, second.Total);
            Assert.True(File.Exists(indexPath));
            Assert.Equal(rebuiltWriteUtc, File.GetLastWriteTimeUtc(indexPath));
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task ListPoolsAsync_AutoRebuildsCorruptIndexWhenPoolDirectoriesExist()
    {
        var root = CreateTempPluginDataFolder();
        var itemId = Guid.NewGuid();

        try
        {
            var store = new PoolStore(root);
            await CreatePool(store, root, itemId, "Movie", "Films");
            await File.WriteAllTextAsync(Path.Combine(PoolRoot(root), "index.json"), "{not json");

            var freshStore = new PoolStore(root);
            var list = await freshStore.ListPoolsAsync(new PoolListQuery(), CancellationToken.None);

            Assert.Equal(1, list.Total);
            Assert.Equal(itemId.ToString(), list.Items.Single().ItemId);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task ListPoolsAsync_MissingIndexAndEmptyRootReturnsEmptyList()
    {
        var root = CreateTempPluginDataFolder();

        try
        {
            var poolRoot = PoolRoot(root);
            Directory.CreateDirectory(poolRoot);
            var store = new PoolStore(root);

            var list = await store.ListPoolsAsync(new PoolListQuery(), CancellationToken.None);

            Assert.Equal(0, list.Total);
            Assert.False(File.Exists(Path.Combine(poolRoot, "index.json")));
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task RebuildIndexAsync_SkipsEmptyPoolDirectories()
    {
        var root = CreateTempPluginDataFolder();
        var filledId = Guid.NewGuid();
        var emptyId = Guid.NewGuid();

        try
        {
            var store = new PoolStore(root);
            await CreatePool(store, root, filledId, "Movie", "Films");
            Directory.CreateDirectory(Path.Combine(PoolRoot(root), emptyId.ToString("N")));
            File.Delete(Path.Combine(PoolRoot(root), "index.json"));

            var rebuild = await store.RebuildIndexAsync(CancellationToken.None);
            var after = await store.ListPoolsAsync(new PoolListQuery(), CancellationToken.None);

            Assert.Equal(1, rebuild.IndexedCount);
            Assert.Equal(1, rebuild.SkippedCount);
            Assert.Equal(1, after.Total);
            Assert.Equal(filledId.ToString(), after.Items.Single().ItemId);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task ListPoolsAsync_UsesOnlyDedicatedIndexForVeryLargeLibraries()
    {
        var root = CreateTempPluginDataFolder();

        try
        {
            var oldRoot = Path.Combine(root, "pools");
            Directory.CreateDirectory(oldRoot);
            await File.WriteAllTextAsync(
                Path.Combine(oldRoot, "index.json"),
                JsonSerializer.Serialize(new PoolIndexDocument
                {
                    Pools = new List<PoolIndexEntry>
                    {
                        new() { ItemId = Guid.NewGuid().ToString(), ItemName = "Old", LibraryName = "Films", UpdatedUtc = DateTimeOffset.UtcNow }
                    }
                }));

            var dedicatedRoot = PoolRoot(root);
            Directory.CreateDirectory(dedicatedRoot);
            var entries = Enumerable.Range(0, 200_000)
                .Select(i => new PoolIndexEntry
                {
                    ItemId = Guid.NewGuid().ToString(),
                    ItemName = $"Movie {i:D5}",
                    ItemType = "Movie",
                    LibraryName = i % 10 == 0 ? "Films" : "Series",
                    ImageCount = 1,
                    SizeBytes = 1024,
                    UpdatedUtc = DateTimeOffset.UtcNow.AddSeconds(-i)
                })
                .ToList();
            await File.WriteAllTextAsync(
                Path.Combine(dedicatedRoot, "index.json"),
                JsonSerializer.Serialize(new PoolIndexDocument { Pools = entries }));

            var store = new PoolStore(root);
            var page = await store.ListPoolsAsync(
                new PoolListQuery { Library = "Films", Start = 200, Limit = 25 },
                CancellationToken.None);

            Assert.Equal(20_000, page.Total);
            Assert.Equal(25, page.Items.Count);
            Assert.DoesNotContain(page.Items, item => item.ItemName == "Old");
            Assert.Single(Directory.EnumerateFileSystemEntries(dedicatedRoot));
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task DeferredIndexWrites_FlushOnlyWhenScopeCompletes()
    {
        var root = CreateTempPluginDataFolder();
        var itemId = Guid.NewGuid();

        try
        {
            var store = new PoolStore(root);
            await using (var _ = await store.BeginDeferredIndexWritesAsync(CancellationToken.None))
            {
                await CreatePool(store, root, itemId, "Movie", "Films");

                var beforeFlush = await store.ListPoolsAsync(new PoolListQuery(), CancellationToken.None);
                Assert.Equal(0, beforeFlush.Total);
                Assert.False(File.Exists(Path.Combine(PoolRoot(root), "index.json")));
            }

            var afterFlush = await store.ListPoolsAsync(new PoolListQuery(), CancellationToken.None);
            Assert.Equal(1, afterFlush.Total);
            Assert.Equal(itemId.ToString(), afterFlush.Items.Single().ItemId);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task DeferredIndexWrites_DoNotLeakThroughCachedIndexBeforeFlush()
    {
        var root = CreateTempPluginDataFolder();
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();

        try
        {
            var store = new PoolStore(root);
            await CreatePool(store, root, firstId, "First", "Films");
            var cached = await store.ListPoolsAsync(new PoolListQuery(), CancellationToken.None);
            Assert.Equal(1, cached.Total);

            await using (var _ = await store.BeginDeferredIndexWritesAsync(CancellationToken.None))
            {
                await CreatePool(store, root, secondId, "Second", "Films");

                var beforeFlush = await store.ListPoolsAsync(new PoolListQuery(), CancellationToken.None);
                Assert.Equal(1, beforeFlush.Total);
                Assert.Equal(firstId.ToString(), beforeFlush.Items.Single().ItemId);
            }

            var afterFlush = await store.ListPoolsAsync(new PoolListQuery(), CancellationToken.None);
            Assert.Equal(2, afterFlush.Total);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task ImportImageAsync_RejectsUnsupportedUpload()
    {
        var root = CreateTempPluginDataFolder();
        var itemId = Guid.NewGuid();

        try
        {
            var store = new PoolStore(root);
            await using var stream = new MemoryStream(new byte[] { 1, 2, 3, 4 });
            await Assert.ThrowsAsync<InvalidDataException>(() => store.ImportImageAsync(
                new PoolItemSnapshot(itemId, "Movie", "Movie", "Films", null),
                stream,
                "bad.txt",
                new Configuration(),
                CancellationToken.None));
            Assert.False(Directory.Exists(PoolRoot(root)));
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task DeleteImageAsync_RemovesImageAndUpdatesIndex()
    {
        var root = CreateTempPluginDataFolder();
        var itemId = Guid.NewGuid();

        try
        {
            var store = new PoolStore(root);
            var poolDir = store.TryGetPoolDirectory(itemId, create: true)!;
            var imagePath = Path.Combine(poolDir, "poster.png");
            await File.WriteAllBytesAsync(imagePath, Png1x1);
            var snapshot = new PoolItemSnapshot(itemId, "Movie", "Movie", "Films", null);
            await store.EnsurePoolAsync(snapshot, poolDir, CancellationToken.None);
            await store.RecordImageAsync(snapshot, poolDir, imagePath, "upload", "unknown", null, "image/png", 1, 1, 99, CancellationToken.None);

            var deleted = await store.DeleteImageAsync(itemId, "poster.png", CancellationToken.None);
            var list = await store.ListPoolsAsync(new PoolListQuery(), CancellationToken.None);

            Assert.Equal("poster.png", deleted.FileName);
            Assert.False(File.Exists(imagePath));
            Assert.False(Directory.Exists(poolDir));
            Assert.Equal(0, list.Total);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task DeleteImageAsync_RejectsPathTraversal()
    {
        var root = CreateTempPluginDataFolder();
        var itemId = Guid.NewGuid();

        try
        {
            var store = new PoolStore(root);
            await CreatePool(store, root, itemId, "Movie", "Films");

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                store.DeleteImageAsync(itemId, "..\\poster.png", CancellationToken.None));
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task CreatePoolDirectoryForWrite_UsesDedicatedRootAndIndexesDownloadedImage()
    {
        var root = CreateTempPluginDataFolder();
        var itemId = Guid.NewGuid();

        try
        {
            var oldRoot = Path.Combine(root, "pools");
            Directory.CreateDirectory(oldRoot);

            var store = new PoolStore(root);
            var poolDir = store.TryCreatePoolDirectoryForWrite(itemId)!;
            var imagePath = Path.Combine(poolDir, "downloaded.png");
            await File.WriteAllBytesAsync(imagePath, Png1x1);
            var snapshot = new PoolItemSnapshot(itemId, "Downloaded", "Movie", "Films", null);

            await store.RecordImageAsync(
                snapshot,
                poolDir,
                imagePath,
                "remote",
                "fr",
                "https://image.tmdb.org/t/p/original/poster.png",
                "image/png",
                1,
                1,
                42,
                CancellationToken.None);

            var dedicatedRoot = PoolRoot(root);
            Assert.Equal(Path.Combine(dedicatedRoot, itemId.ToString("N")), poolDir);
            Assert.True(File.Exists(Path.Combine(dedicatedRoot, "index.json")));
            Assert.True(File.Exists(Path.Combine(poolDir, "pool.json")));
            Assert.False(Directory.Exists(Path.Combine(oldRoot, itemId.ToString("N"))));

            var list = await store.ListPoolsAsync(new PoolListQuery(), CancellationToken.None);
            var entry = Assert.Single(list.Items);
            Assert.Equal(itemId.ToString(), entry.ItemId);
            Assert.Equal(1, entry.ImageCount);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    private static async Task CreatePool(PoolStore store, string root, Guid itemId, string name, string library)
    {
        var poolDir = store.TryGetPoolDirectory(itemId, create: true)!;
        var imagePath = Path.Combine(poolDir, "poster.png");
        await File.WriteAllBytesAsync(imagePath, Png1x1);
        var snapshot = new PoolItemSnapshot(itemId, name, "Movie", library, null);
        await store.EnsurePoolAsync(snapshot, poolDir, CancellationToken.None);
        await store.RecordImageAsync(snapshot, poolDir, imagePath, "upload", "unknown", null, "image/png", 1, 1, 1, CancellationToken.None);
    }

    private static string CreateTempPluginDataFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), "poster-rotator-store-" + Guid.NewGuid().ToString("N"));
        var pluginData = Path.Combine(root, "Jellyfin.Plugin.PosterRotator");
        Directory.CreateDirectory(pluginData);
        return pluginData;
    }

    private static string PoolRoot(string pluginData) =>
        Path.Combine(Directory.GetParent(pluginData)!.FullName, PoolStore.PoolRootDirectoryName);

    private static void DeleteTempRoot(string pluginData)
    {
        var root = Directory.GetParent(pluginData)!.FullName;
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    private static readonly byte[] Png1x1 =
    {
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
        0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
        0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
        0x08, 0x06, 0x00, 0x00, 0x00
    };
}
