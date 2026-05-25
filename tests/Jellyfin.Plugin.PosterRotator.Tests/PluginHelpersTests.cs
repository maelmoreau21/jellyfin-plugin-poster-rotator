using System.Net;
using Jellyfin.Plugin.PosterRotator;
using Jellyfin.Plugin.PosterRotator.Helpers;
using Xunit;

namespace Jellyfin.Plugin.PosterRotator.Tests;

public class PluginHelpersTests
{
    [Fact]
    public void Configuration_DefaultsUsePluginDataAndSafeDownloads()
    {
        var cfg = new Configuration();

        Assert.Equal(PoolStorageMode.PluginData, cfg.PoolStorageMode);
        Assert.Equal(4, cfg.PoolSize);
        Assert.Equal(72, cfg.MinHoursBetweenSwitches);
        Assert.Equal(500, cfg.MaxRotationsPerRun);
        Assert.Equal(250, cfg.MaxDownloadsPerRun);
        Assert.Equal(250, cfg.MaxProviderLookupsPerRun);
        Assert.Equal(250, cfg.ProcessingBatchSize);
        Assert.Equal(25, cfg.MaxDownloadMegabytes);
        Assert.True(cfg.BlockPrivateNetworkImageUrls);
    }

    [Fact]
    public void IsPathInsideOrEqual_DoesNotTreatSiblingAsChild()
    {
        var root = Path.Combine(Path.GetTempPath(), "poster-rotator-root");
        var inside = Path.Combine(root, "child", "movie.mkv");
        var sibling = root + "-other";

        Assert.True(PluginHelpers.IsPathInsideOrEqual(inside, root));
        Assert.True(PluginHelpers.IsPathInsideOrEqual(root, root));
        Assert.False(PluginHelpers.IsPathInsideOrEqual(sibling, root));
    }

    [Fact]
    public void IsSafePoolDirectory_RequiresNamedPoolUnderRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "poster-rotator-test-" + Guid.NewGuid().ToString("N"));
        var pool = Path.Combine(root, ".poster_pool");
        var other = Path.Combine(root, "other");

        try
        {
            Directory.CreateDirectory(pool);
            Directory.CreateDirectory(other);

            Assert.True(PluginHelpers.IsSafePoolDirectory(pool, root));
            Assert.False(PluginHelpers.IsSafePoolDirectory(other, root));
            Assert.False(PluginHelpers.IsSafePoolDirectory(pool, root + "-sibling"));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("file:///tmp/poster.jpg")]
    [InlineData("ftp://example.com/poster.jpg")]
    [InlineData("http://localhost/poster.jpg")]
    [InlineData("http://127.0.0.1/poster.jpg")]
    [InlineData("http://10.0.0.7/poster.jpg")]
    [InlineData("http://172.16.0.7/poster.jpg")]
    [InlineData("http://192.168.1.7/poster.jpg")]
    [InlineData("http://169.254.1.1/poster.jpg")]
    [InlineData("http://[::1]/poster.jpg")]
    [InlineData("http://[fe80::1]/poster.jpg")]
    [InlineData("http://[fc00::1]/poster.jpg")]
    public void IsAllowedRemoteImageUri_BlocksUnsafeTargets(string url)
    {
        Assert.False(PluginHelpers.IsAllowedRemoteImageUri(new Uri(url), blockPrivateNetwork: true));
    }

    [Fact]
    public void IsAllowedRemoteImageUri_AllowsPublicHttpTargets()
    {
        Assert.True(PluginHelpers.IsAllowedRemoteImageUri(new Uri("https://image.tmdb.org/t/p/original/poster.jpg"), blockPrivateNetwork: true));
    }

    [Fact]
    public void IsPrivateOrLocalAddress_BlocksPrivateAddressRanges()
    {
        Assert.True(PluginHelpers.IsPrivateOrLocalAddress(IPAddress.Parse("10.1.2.3")));
        Assert.True(PluginHelpers.IsPrivateOrLocalAddress(IPAddress.Parse("172.31.255.255")));
        Assert.True(PluginHelpers.IsPrivateOrLocalAddress(IPAddress.Parse("192.168.1.1")));
        Assert.True(PluginHelpers.IsPrivateOrLocalAddress(IPAddress.Parse("127.0.0.1")));
        Assert.False(PluginHelpers.IsPrivateOrLocalAddress(IPAddress.Parse("8.8.8.8")));
    }

    [Fact]
    public void TryGetImageFormat_RecognizesPngHeader()
    {
        using var stream = new MemoryStream(new byte[]
        {
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
            0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52
        });

        Assert.True(PluginHelpers.TryGetImageFormat(stream, out var extension, out var mimeType));
        Assert.Equal(".png", extension);
        Assert.Equal("image/png", mimeType);
        Assert.Equal(0, stream.Position);
    }

    [Fact]
    public async Task CopyToFileWithLimitAsync_ThrowsWhenLimitIsExceeded()
    {
        var path = Path.Combine(Path.GetTempPath(), "poster-rotator-copy-" + Guid.NewGuid().ToString("N"));

        try
        {
            await using var source = new MemoryStream(new byte[] { 1, 2, 3, 4, 5 });
            await Assert.ThrowsAsync<InvalidDataException>(
                () => PluginHelpers.CopyToFileWithLimitAsync(source, path, maxBytes: 4, CancellationToken.None));
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
