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

        Assert.Contains("data-i18n=\"Label.MaxRotationsPerRun\"", html);
        Assert.Contains("min=\"0\"", html);
        Assert.Contains("data-i18n=\"Help.MaxRotationsPerRun\"", html);
        Assert.Contains("posterRotatorFieldHelp", html);
        Assert.DoesNotContain("Cadence", html);
        Assert.DoesNotContain("MaxProviderLookupsPerRun", html);
        Assert.DoesNotContain("MaxDownloadsPerRun", html);
        Assert.DoesNotContain("ProcessingBatchSize", html);
        Assert.DoesNotContain("AutoCleanupOrphanedPools", html);
        Assert.DoesNotContain("CleanupIntervalDays", html);
        Assert.Contains("data-i18n=\"Label.MaxPreferredLanguageImages\"", html);
        Assert.Contains("FallbackMode", html);
        Assert.Contains("fallbackModeValue", html);
        Assert.Contains("OriginalThenConfigured", html);
        Assert.Contains("ConfiguredThenOriginal", html);
        Assert.Contains("OriginalOnly", html);
        Assert.Contains("ConfiguredOnly", html);
        Assert.Contains("byId('FallbackMode').value = String(fallbackModeValue(fallbackMode));", html);
        Assert.Contains("cfg.FallbackMode = fallbackModeValue(byId('FallbackMode').value);", html);
        Assert.Contains("data-i18n=\"Checkbox.AllowAnyLanguageFallback\"", html);
        Assert.DoesNotContain("VO en fallback", html);
        Assert.DoesNotContain("RebuildIndexBtn", html);
        Assert.Contains("data-i18n=\"Button.DownloadMissingPools\"", html);
        Assert.Contains("DownloadMissingPoolsBtn", html);
        Assert.Contains("downloadMissingPoolsNow", html);
        Assert.Contains("PosterRotator/Pools/DownloadMissing", html);
        Assert.DoesNotContain("PurgeOrphansBtn", html);
        Assert.Contains("data-i18n=\"Button.PurgeAllPools\"", html);
        Assert.Contains("PosterRotator/PurgeAllPools", html);
        Assert.Contains("purgeAllPools", html);
        Assert.Contains("posterRotatorMediaName", html);
        Assert.Contains("posterRotatorPill", html);
        Assert.DoesNotContain("<h3>Maintenance</h3>", html);
        Assert.Contains("data-i18n=\"Checkbox.SequentialRotation\"", html);
        Assert.Contains("data-i18n=\"Help.SequentialRotation\"", html);
        Assert.Contains("id=\"PoolsLimit\"", html);
        Assert.Contains("<option value=\"25\">25</option>", html);
        Assert.Contains("<option value=\"50\" selected>50</option>", html);
        Assert.Contains("<option value=\"100\">100</option>", html);
        Assert.Contains("<option value=\"200\">200</option>", html);
        Assert.Contains("requestToken", html);
        Assert.Contains("function updatePoolButtonStates()", html);
        Assert.Contains("setDisabled('PrevPoolsBtn', poolsState.start <= 0);", html);
        Assert.Contains("setDisabled('NextPoolsBtn', poolsState.start + poolsState.limit >= poolsState.total);", html);
        Assert.Contains("setDisabled('RotateLibraryBtn', !hasLibrary);", html);
        Assert.Contains("setDisabled('PurgeItemBtn', !hasSelectedPool);", html);
        Assert.Contains("function markSelectedPoolRow()", html);
        Assert.DoesNotContain("return loadPools(false);", html);

        var labelIndex = html.IndexOf("for=\"MaxRotationsPerRun\" data-i18n=\"Label.MaxRotationsPerRun\"", StringComparison.Ordinal);
        var helpIndex = html.IndexOf("data-i18n=\"Help.MaxRotationsPerRun\"", StringComparison.Ordinal);
        var inputIndex = html.IndexOf("<input id=\"MaxRotationsPerRun\"", StringComparison.Ordinal);

        Assert.True(labelIndex >= 0);
        Assert.True(helpIndex > labelIndex);
        Assert.True(inputIndex > helpIndex);
    }

    [Fact]
    public void ConfigPage_ExposesInterfaceLanguageSelectorAndLocalizationEndpoint()
    {
        var html = LoadConfigHtml();

        Assert.Contains("id=\"InterfaceLanguage\"", html);
        Assert.Contains("data-i18n=\"Label.InterfaceLanguage\"", html);
        Assert.Contains("<option value=\"auto\" data-i18n=\"Language.Auto\">", html);
        Assert.Contains("<option value=\"en\" data-i18n=\"Language.English\">", html);
        Assert.Contains("<option value=\"fr\" data-i18n=\"Language.French\">", html);
        Assert.Contains("cfg.InterfaceLanguage = interfaceLanguageValue(textValue('InterfaceLanguage', 'auto'));", html);
        Assert.Contains("loadLocalization(interfaceLanguage)", html);
        Assert.Contains("PosterRotator/Localization?language=", html);
        Assert.Contains("document.querySelectorAll('[data-i18n]')", html);
    }

    [Fact]
    public void ConfigPage_RendersPoolImagePreviewsWithFallback()
    {
        var html = LoadConfigHtml();

        Assert.Contains("poolImageUrl(fileName, image)", html);
        Assert.Contains("poolOriginalImageUrl(fileName, image)", html);
        Assert.Contains("data-original-src", html);
        Assert.Contains("data-original-tried", html);
        Assert.Contains("'ApiKey'", html);
        Assert.DoesNotContain("api_key", html);
        Assert.Contains("appendQuery(url, 'preview', 'true')", html);
        Assert.Contains("appendQuery(url, 'maxWidth', '320')", html);
        Assert.Contains("appendQuery(url, 'maxHeight', '480')", html);
        Assert.Contains("appendQuery(url, 'quality', '80')", html);
        Assert.Contains("posterRotatorThumbImage", html);
        Assert.Contains("posterRotatorThumbError\" hidden", html);
        Assert.Contains("grid-template-columns: repeat(auto-fill, minmax(96px, 104px));", html);
        Assert.Contains("width: 104px !important;", html);
        Assert.Contains("height: 156px !important;", html);
        Assert.Contains("max-height: 156px !important;", html);
        Assert.Contains("object-fit: cover !important;", html);
        Assert.Contains("width=\"104\" height=\"156\"", html);
        Assert.Contains("posterRotatorCurrentBadge", html);
        Assert.Contains("t('Message.Current')", html);
        Assert.Contains("overflow-wrap: anywhere;", html);
        Assert.Contains("-webkit-line-clamp: 3;", html);
        Assert.Contains("prop(image, 'IsCurrent', 'isCurrent', false)", html);
        Assert.Contains("fallback.hidden = true;", html);
        Assert.Contains("fallback.hidden = false;", html);
        Assert.Contains("classList.add('is-hidden')", html);
        Assert.Contains("classList.remove('is-hidden')", html);
        Assert.DoesNotContain("img.style.display", html);
        Assert.Contains("t('Message.PreviewUnavailable')", html);
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
