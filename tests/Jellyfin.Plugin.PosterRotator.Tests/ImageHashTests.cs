using Jellyfin.Plugin.PosterRotator.Helpers;
using Xunit;

namespace Jellyfin.Plugin.PosterRotator.Tests;

public sealed class ImageHashTests
{
    [Fact]
    public async Task ComputeNormalizedHashAsync_FallsBackWithoutImageProcessor()
    {
        var path = Path.Combine(Path.GetTempPath(), "poster-rotator-hash-" + Guid.NewGuid().ToString("N") + ".bin");
        try
        {
            var bytes = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
            await File.WriteAllBytesAsync(path, bytes);

            var direct = ImageHash.ComputeHash(path);
            var normalized = await ImageHash.ComputeNormalizedHashAsync(path, imageProcessor: null, CancellationToken.None);

            Assert.NotEqual((ulong)0, direct);
            Assert.Equal(direct, normalized);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void IsDuplicate_UsesHammingThreshold()
    {
        const ulong hash = 0b1111_0000UL;

        Assert.True(ImageHash.IsDuplicate(hash, new[] { 0b1111_0001UL }, threshold: 1));
        Assert.False(ImageHash.IsDuplicate(hash, new[] { 0b1111_0011UL }, threshold: 1));
        Assert.False(ImageHash.IsDuplicate(0, new[] { hash }));
    }
}
