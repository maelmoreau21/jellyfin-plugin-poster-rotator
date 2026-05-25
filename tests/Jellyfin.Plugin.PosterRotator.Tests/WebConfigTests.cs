using Xunit;

namespace Jellyfin.Plugin.PosterRotator.Tests;

public sealed class WebConfigTests
{
    [Fact]
    public void ConfigPage_UsesTwoPanelsAndLibrarySelect()
    {
        var html = LoadConfigHtml();

        Assert.Contains("data-panel=\"PoolsPanel\"", html);
        Assert.Contains("data-panel=\"SettingsPanel\"", html);
        Assert.Contains("<select id=\"PoolsLibrary\"", html);
        Assert.DoesNotContain("<input id=\"PoolsLibrary\"", html);
    }

    [Fact]
    public void ConfigPage_HidesManualRootsAndClearsLegacySettingOnSave()
    {
        var html = LoadConfigHtml();

        Assert.DoesNotContain("Racines manuelles", html);
        Assert.DoesNotContain("<textarea id=\"ManualLibraryRoots\"", html);
        Assert.Contains("cfg.ManualLibraryRoots = [];", html);
    }

    [Fact]
    public void ConfigPage_UsesSimpleRotationLimitAndNoTechnicalFields()
    {
        var html = LoadConfigHtml();

        Assert.Contains("Nombre maximum d'affiches a changer par passage", html);
        Assert.Contains("min=\"0\"", html);
        Assert.DoesNotContain("Cadence", html);
        Assert.DoesNotContain("Affiches par pool", html);
        Assert.DoesNotContain("Heures entre changements", html);
        Assert.DoesNotContain("MaxProviderLookupsPerRun", html);
        Assert.DoesNotContain("MaxDownloadsPerRun", html);
        Assert.DoesNotContain("ProcessingBatchSize", html);
        Assert.DoesNotContain("AutoCleanupOrphanedPools", html);
        Assert.DoesNotContain("CleanupIntervalDays", html);
        Assert.Contains("Affiches max en langue preferee par pool", html);
        Assert.Contains("Reparer la liste des pools", html);
    }

    [Fact]
    public void ConfigPage_RendersPoolImagePreviewsWithFallback()
    {
        var html = LoadConfigHtml();

        Assert.Contains("poolImageUrl(fileName, image)", html);
        Assert.Contains("Apercu indisponible", html);
        Assert.Contains("data-image-delete", html);
    }

    private static string LoadConfigHtml()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            var path = Path.Combine(
                directory.FullName,
                "src",
                "Jellyfin.Plugin.PosterRotator",
                "Web",
                "config.html");
            if (File.Exists(path))
                return File.ReadAllText(path);

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Unable to locate Web/config.html from the test output directory.");
    }
}
