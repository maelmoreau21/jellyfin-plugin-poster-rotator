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

    [Fact]
    public void ComputeDHashFromLuminance_FlatColorProducesZeroHash()
    {
        var luminance = new byte[72];
        Array.Fill(luminance, (byte)128);

        var hash = ImageHash.ComputeDHashFromLuminance(luminance);

        Assert.Equal(0UL, hash);
    }

    [Fact]
    public void ComputeDHashFromLuminance_DescendingGradientProducesAllOnes()
    {
        // 9 wide x 8 tall. Each row: 9, 8, 7, 6, 5, 4, 3, 2, 1
        // col > col+1 is true for all 8 comparisons in all 8 rows -> 64 ones
        var luminance = new byte[72];
        for (var row = 0; row < 8; row++)
        {
            for (var col = 0; col < 9; col++)
            {
                luminance[(row * 9) + col] = (byte)(200 - (col * 20));
            }
        }

        var hash = ImageHash.ComputeDHashFromLuminance(luminance);

        Assert.Equal(ulong.MaxValue, hash);
    }

    [Fact]
    public void ComputeDHashFromLuminance_AscendingGradientProducesAllZeros()
    {
        // 9 wide x 8 tall. Each row: 1, 2, 3, 4, 5, 6, 7, 8, 9
        // col > col+1 is false for all comparisons -> 0
        var luminance = new byte[72];
        for (var row = 0; row < 8; row++)
        {
            for (var col = 0; col < 9; col++)
            {
                luminance[(row * 9) + col] = (byte)(10 + (col * 20));
            }
        }

        var hash = ImageHash.ComputeDHashFromLuminance(luminance);

        Assert.Equal(0UL, hash);
    }

    [Fact]
    public void ComputeDHash_NonExistentFileReturnsZero()
    {
        var hash = ImageHash.ComputeDHash("C:\\does_not_exist_image_file.jpg");
        Assert.Equal(0UL, hash);
    }
}
