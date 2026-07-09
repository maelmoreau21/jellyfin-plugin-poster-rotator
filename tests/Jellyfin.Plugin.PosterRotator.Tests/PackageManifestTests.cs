using System.Text.Json;
using Xunit;

namespace Jellyfin.Plugin.PosterRotator.Tests;

public sealed class PackageManifestTests
{
    [Fact]
    public void LocalMetaJson_DeclaresPluginImageForManualInstalls()
    {
        var meta = JsonDocument.Parse(File.ReadAllText(FindRepoFile("meta.json"))).RootElement;

        Assert.Equal("7f6eea8b-0e9c-4cbd-9d2a-31f9a37ce2b7", meta.GetProperty("guid").GetString());
        Assert.Equal("1.8.0.0", meta.GetProperty("version").GetString());
        Assert.Equal("12.0.0.0", meta.GetProperty("targetAbi").GetString());
        Assert.Equal("jellyfin-plugin-posterrotator.png", meta.GetProperty("imagePath").GetString());
    }

    [Fact]
    public void ManifestJson_DeclaresLatestJellyfin12Release()
    {
        var manifest = JsonDocument.Parse(File.ReadAllText(FindRepoFile("manifest.json"))).RootElement;
        var plugin = manifest[0];
        var latest = plugin.GetProperty("versions")[0];

        Assert.Equal("1.8.0.0", latest.GetProperty("version").GetString());
        Assert.Equal("12.0.0.0", latest.GetProperty("targetAbi").GetString());
        Assert.Contains("v1.8.0.0", latest.GetProperty("sourceUrl").GetString());
        var checksum = latest.GetProperty("checksum").GetString();
        Assert.NotNull(checksum);
        Assert.Equal(32, checksum.Length);
        Assert.Matches("^[0-9a-fA-F]{32}$", checksum);
    }

    private static string FindRepoFile(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            var path = Path.Combine(directory.FullName, fileName);
            if (File.Exists(path))
                return path;

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Unable to locate " + fileName + " from the test output directory.");
    }
}
