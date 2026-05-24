using System.Text.Json;
using Jellyfin.Plugin.PosterRotator.Helpers;
using Xunit;

namespace Jellyfin.Plugin.PosterRotator.Tests;

public sealed class PoolStoreTests
{
    [Fact]
    public async Task EnsurePoolAsync_MigratesLegacyMapsAndDeletesThemAfterPoolJsonIsWritten()
    {
        var root = CreateTempRoot();
        var itemId = Guid.NewGuid();
        var poolDir = Path.Combine(root, "pools", itemId.ToString("N"));
        Directory.CreateDirectory(poolDir);
        var imagePath = Path.Combine(poolDir, "poster.png");
        await File.WriteAllBytesAsync(imagePath, Png1x1);

        var state = new RotationState();
        state.LastIndexByItem[itemId.ToString()] = 4;
        state.LastRotatedUtcByItem[itemId.ToString()] = 1_700_000_000;
        await File.WriteAllTextAsync(Path.Combine(poolDir, "rotation_state.json"), JsonSerializer.Serialize(state));
        await File.WriteAllTextAsync(Path.Combine(poolDir, "pool_languages.json"), JsonSerializer.Serialize(new Dictionary<string, string> { ["poster.png"] = "fr" }));
        await File.WriteAllTextAsync(Path.Combine(poolDir, "pool_urls.json"), JsonSerializer.Serialize(new Dictionary<string, string> { ["poster.png"] = "https://example.invalid/poster.png" }));
        await File.WriteAllTextAsync(Path.Combine(poolDir, "pool_hashes.json"), JsonSerializer.Serialize(new Dictionary<string, ulong> { ["poster.png"] = 123 }));

        try
        {
            var store = new PoolStore(root);
            var pool = await store.EnsurePoolAsync(
                new PoolItemSnapshot(itemId, "Movie A", "Movie", "Films", "D:\\Media\\Movie A.mkv"),
                poolDir,
                CancellationToken.None);

            Assert.True(File.Exists(Path.Combine(poolDir, "pool.json")));
            Assert.False(File.Exists(Path.Combine(poolDir, "rotation_state.json")));
            Assert.False(File.Exists(Path.Combine(poolDir, "pool_languages.json")));
            Assert.False(File.Exists(Path.Combine(poolDir, "pool_urls.json")));
            Assert.False(File.Exists(Path.Combine(poolDir, "pool_hashes.json")));
            Assert.Equal(4, pool.LastIndex);
            Assert.Single(pool.Images);
            Assert.Equal("fr", pool.Images[0].Language);
            Assert.Equal("https://example.invalid/poster.png", pool.Images[0].SourceUrl);
            Assert.Equal((ulong)123, pool.Images[0].Hash);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task PurgeAsync_CanDeleteOnlyOneLibrary()
    {
        var root = CreateTempRoot();
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
            Assert.False(Directory.Exists(Path.Combine(root, "pools", filmsId.ToString("N"))));
            Assert.True(Directory.Exists(Path.Combine(root, "pools", seriesId.ToString("N"))));
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task PurgeAsync_CanDeleteOnlyOrphans()
    {
        var root = CreateTempRoot();
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
            Assert.True(Directory.Exists(Path.Combine(root, "pools", liveId.ToString("N"))));
            Assert.False(Directory.Exists(Path.Combine(root, "pools", orphanId.ToString("N"))));
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task ImportImageAsync_RejectsUnsupportedUpload()
    {
        var root = CreateTempRoot();
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
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task DeleteImageAsync_RemovesImageAndUpdatesIndex()
    {
        var root = CreateTempRoot();
        var itemId = Guid.NewGuid();

        try
        {
            var store = new PoolStore(root);
            var poolDir = Path.Combine(root, "pools", itemId.ToString("N"));
            Directory.CreateDirectory(poolDir);
            var imagePath = Path.Combine(poolDir, "poster.png");
            await File.WriteAllBytesAsync(imagePath, Png1x1);
            var snapshot = new PoolItemSnapshot(itemId, "Movie", "Movie", "Films", null);
            await store.EnsurePoolAsync(snapshot, poolDir, CancellationToken.None);
            await store.RecordImageAsync(snapshot, poolDir, imagePath, "upload", "unknown", null, "image/png", 1, 1, 99, CancellationToken.None);

            var deleted = await store.DeleteImageAsync(itemId, "poster.png", CancellationToken.None);
            var list = await store.ListPoolsAsync(new PoolListQuery(), CancellationToken.None);

            Assert.Equal("poster.png", deleted.FileName);
            Assert.False(File.Exists(imagePath));
            Assert.Equal(0, list.Items.Single().ImageCount);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task DeleteImageAsync_RejectsPathTraversal()
    {
        var root = CreateTempRoot();
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

    private static async Task CreatePool(PoolStore store, string root, Guid itemId, string name, string library)
    {
        var poolDir = Path.Combine(root, "pools", itemId.ToString("N"));
        Directory.CreateDirectory(poolDir);
        var imagePath = Path.Combine(poolDir, "poster.png");
        await File.WriteAllBytesAsync(imagePath, Png1x1);
        var snapshot = new PoolItemSnapshot(itemId, name, "Movie", library, null);
        await store.EnsurePoolAsync(snapshot, poolDir, CancellationToken.None);
        await store.RecordImageAsync(snapshot, poolDir, imagePath, "upload", "unknown", null, "image/png", 1, 1, 1, CancellationToken.None);
    }

    private static string CreateTempRoot() =>
        Path.Combine(Path.GetTempPath(), "poster-rotator-store-" + Guid.NewGuid().ToString("N"));

    private static void DeleteTempRoot(string root)
    {
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
