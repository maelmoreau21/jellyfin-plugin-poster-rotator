using Xunit;

namespace Jellyfin.Plugin.PosterRotator.Tests;

public sealed class PoolImagePreviewTests
{
    [Fact]
    public void PoolImageEndpoint_ServesReducedPreviewWhenRequested()
    {
        var source = LoadControllerSource();

        Assert.Contains("[FromQuery] bool preview = false", source);
        Assert.Contains("[FromQuery] int maxWidth = 320", source);
        Assert.Contains("[FromQuery] int maxHeight = 480", source);
        Assert.Contains("[FromQuery] int quality = 80", source);
        Assert.Contains("IImageProcessor", source);
        Assert.Contains("ProcessImage(new ImageProcessingOptions", source);
        Assert.Contains("MaxWidth = maxWidth", source);
        Assert.Contains("MaxHeight = maxHeight", source);
        Assert.Contains("Quality = quality", source);
        Assert.Contains("originalTooLarge", source);
        Assert.Contains("Preview unavailable.", source);
    }

    private static string LoadControllerSource()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            var path = Path.Combine(
                directory.FullName,
                "src",
                "Jellyfin.Plugin.PosterRotator",
                "Api",
                "PurgeController.cs");
            if (File.Exists(path))
                return File.ReadAllText(path);

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Unable to locate Api/PurgeController.cs from the test output directory.");
    }
}
