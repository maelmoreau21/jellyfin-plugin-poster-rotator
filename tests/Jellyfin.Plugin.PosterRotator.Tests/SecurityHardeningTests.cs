using Xunit;

namespace Jellyfin.Plugin.PosterRotator.Tests;

public sealed class SecurityHardeningTests
{
    [Fact]
    public void RemoteDownloads_DisableAutomaticRedirectsAndRevalidateRedirectLocations()
    {
        var serviceRegistration = ReadRepoFile("ServiceRegistrator.cs");
        var service = ReadRepoFile("PosterRotatorService.cs");

        Assert.Contains("AllowAutoRedirect = false", serviceRegistration);
        Assert.Contains("MaxRemoteImageRedirects", service);
        Assert.Contains("ResolveRemoteImageRedirectUri", service);
        Assert.Contains("IsAllowedRemoteImageUrlAsync(currentUrl, cfg, ct)", service);
        Assert.Contains("SendRemoteImageRequestAsync(client, url, cfg, ct)", service);
    }

    [Fact]
    public void UploadEndpoint_RejectsFilesAboveConfiguredLimitBeforeOpeningStream()
    {
        var controller = ReadRepoFile(Path.Combine("Api", "PurgeController.cs"));
        var lengthCheck = controller.IndexOf("file.Length > maxUploadBytes", StringComparison.Ordinal);
        var openStream = controller.IndexOf("file.OpenReadStream()", StringComparison.Ordinal);

        Assert.Contains("Math.Clamp(cfg.MaxDownloadMegabytes, 1, 200)", controller);
        Assert.True(lengthCheck > 0);
        Assert.True(openStream > lengthCheck);
    }

    [Fact]
    public void DownloadMissingPools_UsesDedicatedPluginDataPoolCreation()
    {
        var service = ReadRepoFile("PosterRotatorService.cs");

        Assert.Contains("DownloadMissingPoolsNowAsync", service);
        Assert.Contains("TryCreatePoolDirectoryForWrite(poolItem!.ItemId)", service);
        Assert.Contains("forcePluginDataStorage: true", service);

        var pluginDataReturn = service.IndexOf("return _poolStore.TryGetPoolDirectory(item.Id, create: false);", StringComparison.Ordinal);
        var legacyFallback = service.LastIndexOf("return legacyPoolDir;", StringComparison.Ordinal);
        Assert.True(pluginDataReturn > 0);
        Assert.True(legacyFallback < pluginDataReturn);
    }

    [Fact]
    public void AdminApi_ExposesManualDownloadMissingPoolsAction()
    {
        var controller = ReadRepoFile(Path.Combine("Api", "PurgeController.cs"));

        Assert.Contains("[HttpPost(\"Pools/DownloadMissing\")]", controller);
        Assert.Contains("DownloadMissingPoolsNowAsync", controller);
    }

    private static string ReadRepoFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            var path = Path.Combine(directory.FullName, "src", "Jellyfin.Plugin.PosterRotator", relativePath);
            if (File.Exists(path))
                return File.ReadAllText(path);

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Unable to locate " + relativePath + " from the test output directory.");
    }
}
