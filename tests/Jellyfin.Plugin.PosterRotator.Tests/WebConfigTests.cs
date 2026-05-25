using Xunit;

namespace Jellyfin.Plugin.PosterRotator.Tests;

public sealed class WebConfigTests
{
    [Fact]
    public void ConfigPage_UsesTwoPanelsAndLibrarySelect()
    {
        var html = LoadConfigHtml();

        Assert.Contains("class=\"posterRotatorTabs\" role=\"tablist\"", html);
        Assert.Contains("id=\"PoolsTab\"", html);
        Assert.Contains("role=\"tab\" aria-selected=\"true\" aria-controls=\"PoolsPanel\"", html);
        Assert.Contains("data-panel=\"PoolsPanel\"", html);
        Assert.Contains("id=\"SettingsTab\"", html);
        Assert.Contains("role=\"tab\" aria-selected=\"false\" aria-controls=\"SettingsPanel\" tabindex=\"-1\"", html);
        Assert.Contains("data-panel=\"SettingsPanel\"", html);
        Assert.Contains("id=\"PoolsPanel\" class=\"posterRotatorPanel is-active\" role=\"tabpanel\" aria-labelledby=\"PoolsTab\" aria-hidden=\"false\"", html);
        Assert.Contains("id=\"SettingsPanel\" class=\"posterRotatorPanel\" role=\"tabpanel\" aria-labelledby=\"SettingsTab\" aria-hidden=\"true\" hidden", html);
        Assert.Contains(".posterRotatorPanel[hidden]", html);
        Assert.Contains("panel.hidden = !active;", html);
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
        Assert.Contains("(0 = aucune limite de nombre. Le delai interne entre deux changements reste respecte.)", html);
        Assert.Contains("posterRotatorFieldHelp", html);
        Assert.DoesNotContain("Cadence", html);
        Assert.DoesNotContain("Affiches par pool", html);
        Assert.DoesNotContain("Heures entre changements", html);
        Assert.DoesNotContain("MaxProviderLookupsPerRun", html);
        Assert.DoesNotContain("MaxDownloadsPerRun", html);
        Assert.DoesNotContain("ProcessingBatchSize", html);
        Assert.DoesNotContain("AutoCleanupOrphanedPools", html);
        Assert.DoesNotContain("CleanupIntervalDays", html);
        Assert.Contains("Affiches max en langue preferee par pool", html);
        Assert.Contains("FallbackMode", html);
        Assert.Contains("Autoriser toutes les langues en dernier recours", html);
        Assert.DoesNotContain("VO en fallback", html);
        Assert.Contains("Reparer la liste des pools", html);

        var labelIndex = html.IndexOf("for=\"MaxRotationsPerRun\">Nombre maximum d'affiches a changer par passage", StringComparison.Ordinal);
        var helpIndex = html.IndexOf("(0 = aucune limite de nombre. Le delai interne entre deux changements reste respecte.)", StringComparison.Ordinal);
        var inputIndex = html.IndexOf("<input id=\"MaxRotationsPerRun\"", StringComparison.Ordinal);

        Assert.True(labelIndex >= 0);
        Assert.True(helpIndex > labelIndex);
        Assert.True(inputIndex > helpIndex);
    }

    [Fact]
    public void ConfigPage_RendersPoolImagePreviewsWithFallback()
    {
        var html = LoadConfigHtml();

        Assert.Contains("poolImageUrl(fileName, image)", html);
        Assert.Contains("'ApiKey'", html);
        Assert.DoesNotContain("api_key", html);
        Assert.Contains("posterRotatorThumbImage", html);
        Assert.Contains("posterRotatorThumbError\" hidden", html);
        Assert.Contains("grid-template-columns: repeat(auto-fill, minmax(112px, 132px));", html);
        Assert.Contains("height: 198px;", html);
        Assert.Contains("max-height: 198px;", html);
        Assert.Contains("object-fit: cover;", html);
        Assert.Contains("fallback.hidden = true;", html);
        Assert.Contains("fallback.hidden = false;", html);
        Assert.Contains("classList.add('is-hidden')", html);
        Assert.Contains("classList.remove('is-hidden')", html);
        Assert.DoesNotContain("img.style.display", html);
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
