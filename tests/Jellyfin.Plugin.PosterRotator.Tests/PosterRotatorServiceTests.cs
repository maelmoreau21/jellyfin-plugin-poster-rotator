using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Xunit;

namespace Jellyfin.Plugin.PosterRotator.Tests;

public class PosterRotatorServiceTests
{
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
