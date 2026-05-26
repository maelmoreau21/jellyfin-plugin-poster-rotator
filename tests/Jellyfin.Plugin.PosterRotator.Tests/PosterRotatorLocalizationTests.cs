using Xunit;

namespace Jellyfin.Plugin.PosterRotator.Tests;

public sealed class PosterRotatorLocalizationTests
{
    [Theory]
    [InlineData("auto", "fr-FR", "fr")]
    [InlineData("auto", "de-DE", "en")]
    [InlineData("fr", "en-US", "fr")]
    [InlineData("es", "fr-FR", "en")]
    [InlineData("", "fr-FR", "fr")]
    public void ResolveLanguage_UsesConfiguredOrServerCultureWithEnglishFallback(
        string configuredLanguage,
        string serverCulture,
        string expected)
    {
        var localization = new PosterRotatorLocalization(() => serverCulture);

        Assert.Equal(expected, localization.ResolveLanguage(configuredLanguage));
    }

    [Theory]
    [InlineData("fr-FR", "fr")]
    [InlineData("fr_CA", "fr")]
    [InlineData("en-US", "en")]
    [InlineData("auto", "auto")]
    [InlineData(" original ", "original")]
    public void NormalizeLanguageCode_UsesBaseLanguage(string language, string expected)
    {
        Assert.Equal(expected, PosterRotatorLocalization.NormalizeLanguageCode(language));
    }

    [Fact]
    public void GetResponse_ReturnsResolvedLanguageOptionsAndTranslatedStrings()
    {
        var localization = new PosterRotatorLocalization(() => "fr-FR");

        var response = localization.GetResponse("auto");

        Assert.Equal("fr", response.Language);
        Assert.Contains(response.Languages, option => option.Value == "auto" && option.Name.Contains("Jellyfin"));
        Assert.Equal("Parametres", response.Strings["Tab.Settings"]);
        Assert.Equal("Rotate pools", localization.Translate("Task.RotatePools.Name", "en"));
        Assert.Equal("Rotation des pools", localization.Translate("Task.RotatePools.Name", "fr"));
    }
}
