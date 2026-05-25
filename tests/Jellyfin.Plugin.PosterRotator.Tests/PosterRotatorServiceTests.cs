using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using System.Net;
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
        Assert.False(budget.HasDownloadWorkRemaining);
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

    [Fact]
    public void RotationRunBudget_StopsDownloadWorkWhenEitherBudgetIsExhausted()
    {
        var budget = new PosterRotatorService.RotationRunBudget(new Configuration
        {
            MaxRotationsPerRun = 0,
            MaxDownloadsPerRun = 1,
            MaxProviderLookupsPerRun = 1
        });

        Assert.True(budget.HasDownloadWorkRemaining);
        Assert.True(budget.TryUseDownloadSlot());
        Assert.False(budget.HasDownloadWorkRemaining);
    }

    [Fact]
    public void RemoteImageRedirectHelpers_ResolveRelativeRedirectsAndClassify3xx()
    {
        var redirect = PosterRotatorService.ResolveRemoteImageRedirectUri(
            "https://images.example.test/path/poster.jpg",
            new Uri("../next.jpg", UriKind.Relative));

        Assert.True(PosterRotatorService.IsRedirectStatusCode(HttpStatusCode.Redirect));
        Assert.False(PosterRotatorService.IsRedirectStatusCode(HttpStatusCode.OK));
        Assert.Equal("https://images.example.test/next.jpg", redirect?.AbsoluteUri);
        Assert.Null(PosterRotatorService.ResolveRemoteImageRedirectUri("not a uri", new Uri("/next.jpg", UriKind.Relative)));
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

    [Fact]
    public void SelectRemoteImagesForLanguage_UsesPreferredQuotaThenOriginalThenConfigured()
    {
        var images = new[]
        {
            Image("fr-1", "fr"),
            Image("fr-2", "fr"),
            Image("ja-1", "ja"),
            Image("en-1", "en")
        };
        var cfg = new Configuration
        {
            PreferredLanguage = "fr",
            MaxPreferredLanguageImages = 2,
            FallbackLanguage = "en",
            FallbackMode = LanguageFallbackMode.OriginalThenConfigured,
            IncludeUnknownLanguage = false,
            AllowAnyLanguageFallback = false
        };

        var selected = PosterRotatorService.SelectRemoteImagesForLanguage(images, cfg, "ja", remainingPreferredSlots: 1);

        Assert.Equal(new[] { "fr-1", "ja-1", "en-1" }, selected.Select(candidate => candidate.Image.Url));
        Assert.Equal(new[] { "fr", "ja", "en" }, selected.Select(candidate => candidate.Language));
    }

    [Fact]
    public void SelectRemoteImagesForLanguage_CanUseConfiguredBeforeOriginal()
    {
        var images = new[]
        {
            Image("fr-1", "fr"),
            Image("ja-1", "ja"),
            Image("en-1", "en")
        };
        var cfg = new Configuration
        {
            PreferredLanguage = "fr",
            FallbackLanguage = "en",
            FallbackMode = LanguageFallbackMode.ConfiguredThenOriginal,
            IncludeUnknownLanguage = false,
            AllowAnyLanguageFallback = false
        };

        var selected = PosterRotatorService.SelectRemoteImagesForLanguage(images, cfg, "ja", remainingPreferredSlots: 0);

        Assert.Equal(new[] { "en-1", "ja-1" }, selected.Select(candidate => candidate.Image.Url));
    }

    [Fact]
    public void SelectRemoteImagesForLanguage_CanIncludeUnknownAndAnyLanguageFallbacks()
    {
        var images = new[]
        {
            Image("unknown-1", null),
            Image("de-1", "de"),
            Image("thumb-es", "es", ImageType.Thumb)
        };
        var cfg = new Configuration
        {
            PreferredLanguage = "fr",
            FallbackLanguage = string.Empty,
            FallbackMode = LanguageFallbackMode.ConfiguredOnly,
            IncludeUnknownLanguage = true,
            AllowAnyLanguageFallback = true
        };

        var selected = PosterRotatorService.SelectRemoteImagesForLanguage(images, cfg, originalLanguage: null, remainingPreferredSlots: 2);

        Assert.Equal(new[] { "unknown-1", "de-1", "thumb-es" }, selected.Select(candidate => candidate.Image.Url));
        Assert.Equal(new[] { "unknown", "de", "es" }, selected.Select(candidate => candidate.Language));
    }

    [Fact]
    public void NormalizeLanguageCode_UsesBaseLanguageAndRejectsUnknownValues()
    {
        Assert.Equal("fr", PosterRotatorService.NormalizeLanguageCode("fr-FR"));
        Assert.Equal("ja", PosterRotatorService.NormalizeLanguageCode(" JA "));
        Assert.Null(PosterRotatorService.NormalizeLanguageCode(""));
        Assert.Null(PosterRotatorService.NormalizeLanguageCode("original"));
    }

    private static RemoteImageInfo Image(string url, string? language, ImageType type = ImageType.Primary) =>
        new()
        {
            Url = url,
            Language = language,
            Type = type,
            ProviderName = "TMDb"
        };
}
