using System.Collections.Generic;

namespace Jellyfin.Plugin.PosterRotator;

public interface IPosterRotatorLocalization
{
    string ResolveLanguage(string? configuredLanguage = null);

    string Translate(string key, string? language = null);

    PosterRotatorLocalizationResponse GetResponse(string? language = null);
}

public sealed class PosterRotatorLocalizationResponse
{
    public string Language { get; set; } = PosterRotatorLocalization.FallbackLanguage;

    public List<PosterRotatorLanguageOption> Languages { get; set; } = new();

    public Dictionary<string, string> Strings { get; set; } = new();
}

public sealed class PosterRotatorLanguageOption
{
    public string Value { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
}
