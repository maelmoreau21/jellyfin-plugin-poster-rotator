using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Xunit;

namespace Jellyfin.Plugin.PosterRotator.Tests;

public class PosterRotatorServiceTests
{
    [Fact]
    public void IsRotationDue_RespectsConfiguredCooldown()
    {
        var now = DateTimeOffset.Parse("2026-05-25T12:00:00Z");

        Assert.False(PosterRotatorService.IsRotationDue(now.AddHours(-12), now, 72));
        Assert.True(PosterRotatorService.IsRotationDue(now.AddHours(-72), now, 72));
        Assert.True(PosterRotatorService.IsRotationDue(null, now, 72));
    }

    [Fact]
    public void RotationRunBudget_StopsAtConfiguredLimits()
    {
        var budget = new PosterRotatorService.RotationRunBudget(new Configuration
        {
            MaxRotationsPerRun = 1,
            MaxDownloadsPerRun = 1,
            MaxProviderLookupsPerRun = 1
        });

        Assert.True(budget.HasRotationSlots);
        budget.RecordRotation();
        Assert.False(budget.HasRotationSlots);

        Assert.True(budget.TryUseDownloadSlot());
        Assert.False(budget.TryUseDownloadSlot());

        Assert.True(budget.TryUseProviderLookupSlot());
        Assert.False(budget.TryUseProviderLookupSlot());
        Assert.False(budget.HasWorkRemaining);
    }

    [Fact]
    public void RotationRunBudget_AllowsZeroRotationLimitAsUnlimited()
    {
        var budget = new PosterRotatorService.RotationRunBudget(new Configuration
        {
            MaxRotationsPerRun = 0,
            MaxDownloadsPerRun = 1,
            MaxProviderLookupsPerRun = 1
        });

        for (var i = 0; i < 1000; i++)
            budget.RecordRotation();

        Assert.True(budget.HasRotationSlots);
        Assert.Equal(1000, budget.Rotations);
        Assert.Equal(int.MaxValue, PosterRotatorService.NormalizeRotationRunLimit(0, 500));
    }

    [Theory]
    [InlineData(0, 250)]
    [InlineData(5, 10)]
    [InlineData(6000, 5000)]
    public void NormalizeProcessingBatchSize_ClampsToSafeRange(int value, int expected)
    {
        Assert.Equal(expected, PosterRotatorService.NormalizeProcessingBatchSize(value));
    }

    [Fact]
    public void ShuffleItemIds_KeepsTheSameIds()
    {
        var ids = Enumerable.Range(0, 25).Select(_ => Guid.NewGuid()).ToArray();
        var original = ids.ToHashSet();

        PosterRotatorService.ShuffleItemIds(ids);

        Assert.Equal(original, ids.ToHashSet());
    }

    [Fact]
    public void OrderRemoteImagesForDownload_PrioritizesPreferredProvidersThenImageType()
    {
        var images = new[]
        {
            new RemoteImageInfo { ProviderName = "Fanart", Type = ImageType.Primary },
            new RemoteImageInfo { ProviderName = "TMDb", Type = ImageType.Thumb },
            new RemoteImageInfo { ProviderName = "TheTVDB", Type = ImageType.Thumb },
            new RemoteImageInfo { ProviderName = "TMDb", Type = ImageType.Primary },
            new RemoteImageInfo { ProviderName = "Other", Type = ImageType.Primary }
        };

        var ordered = PosterRotatorService.OrderRemoteImagesForDownload(images);

        Assert.Collection(
            ordered,
            info =>
            {
                Assert.Equal("TheTVDB", info.ProviderName);
                Assert.Equal(ImageType.Thumb, info.Type);
            },
            info =>
            {
                Assert.Equal("TMDb", info.ProviderName);
                Assert.Equal(ImageType.Primary, info.Type);
            },
            info =>
            {
                Assert.Equal("TMDb", info.ProviderName);
                Assert.Equal(ImageType.Thumb, info.Type);
            },
            info =>
            {
                Assert.Equal("Fanart", info.ProviderName);
                Assert.Equal(ImageType.Primary, info.Type);
            },
            info =>
            {
                Assert.Equal("Other", info.ProviderName);
                Assert.Equal(ImageType.Primary, info.Type);
            });
    }
}
