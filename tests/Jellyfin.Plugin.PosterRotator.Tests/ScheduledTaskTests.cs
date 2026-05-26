using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.PosterRotator.Tests;

public sealed class ScheduledTaskTests
{
    [Fact]
    public void PosterRotationTask_UsesPosterRotatorCategoryAndStableKey()
    {
        var task = new PosterRotationTask(
            new FakePosterRotatorService(),
            NullLogger<PosterRotationTask>.Instance,
            new PosterRotatorLocalization(() => "en-US"));

        Assert.Equal("Rotate pools", task.Name);
        Assert.Contains("existing local pools", task.Description);
        Assert.Equal("Poster Rotator", task.Category);
        Assert.Equal("PosterRotator.RotatePostersTask", task.Key);
        Assert.Contains(task.GetDefaultTriggers(), trigger => trigger.Type == TaskTriggerInfoType.DailyTrigger);
    }

    [Fact]
    public void DownloadMissingPoolsTask_UsesPosterRotatorCategoryAndStableKey()
    {
        var task = new DownloadMissingPoolsTask(
            new FakePosterRotatorService(),
            NullLogger<DownloadMissingPoolsTask>.Instance,
            new PosterRotatorLocalization(() => "en-US"));

        Assert.Equal("Download missing pools", task.Name);
        Assert.Contains("missing or incomplete", task.Description);
        Assert.Equal("Poster Rotator", task.Category);
        Assert.Equal("PosterRotator.DownloadMissingPoolsTask", task.Key);
        Assert.Contains(
            task.GetDefaultTriggers(),
            trigger => trigger.Type == TaskTriggerInfoType.DailyTrigger
                && trigger.TimeOfDayTicks == TimeSpan.FromHours(2).Ticks);
    }

    [Fact]
    public void ScheduledTasks_CallSeparatedServiceMethods()
    {
        var rotationSource = File.ReadAllText(FindRepoFile("PosterRotationTask.cs"));
        var downloadSource = File.ReadAllText(FindRepoFile("DownloadMissingPoolsTask.cs"));

        Assert.Contains("RunRotationAsync", rotationSource);
        Assert.DoesNotContain("DownloadMissingPoolsAsync", rotationSource);
        Assert.Contains("DownloadMissingPoolsAsync", downloadSource);
        Assert.DoesNotContain("RunRotationAsync", downloadSource);
    }

    [Fact]
    public async Task OrphanPoolCleanupTask_PurgesOnlyOrphans()
    {
        var service = new FakePosterRotatorService();
        var task = new OrphanPoolCleanupTask(
            service,
            NullLogger<OrphanPoolCleanupTask>.Instance,
            new PosterRotatorLocalization(() => "fr-FR"));
        var progress = new ProgressRecorder();

        await task.ExecuteAsync(progress, CancellationToken.None);

        Assert.Equal("Nettoyage des pools orphelins", task.Name);
        Assert.Equal("Poster Rotator", task.Category);
        Assert.Equal("PosterRotator.CleanupOrphanPools", task.Key);
        Assert.Equal("orphans", service.LastPurgeRequest?.Scope);
        Assert.Equal(100, progress.LastValue);
        Assert.Contains(task.GetDefaultTriggers(), trigger => trigger.Type == TaskTriggerInfoType.WeeklyTrigger);
    }

    [Fact]
    public void ScheduledTasks_LocalizeNamesAndDescriptions()
    {
        var service = new FakePosterRotatorService();
        var english = new PosterRotatorLocalization(() => "en-US");
        var french = new PosterRotatorLocalization(() => "fr-FR");

        var englishRotation = new PosterRotationTask(service, NullLogger<PosterRotationTask>.Instance, english);
        var frenchRotation = new PosterRotationTask(service, NullLogger<PosterRotationTask>.Instance, french);
        var englishDownload = new DownloadMissingPoolsTask(service, NullLogger<DownloadMissingPoolsTask>.Instance, english);
        var frenchDownload = new DownloadMissingPoolsTask(service, NullLogger<DownloadMissingPoolsTask>.Instance, french);

        Assert.Equal("Rotate pools", englishRotation.Name);
        Assert.Equal("Rotation des pools", frenchRotation.Name);
        Assert.Equal("Download missing pools", englishDownload.Name);
        Assert.Equal("Telechargement des pools manquants", frenchDownload.Name);
        Assert.Contains("Rotates eligible posters", englishRotation.Description);
        Assert.Contains("Remplace les affiches", frenchRotation.Description);
    }

    private sealed class FakePosterRotatorService : IPosterRotatorService
    {
        public PoolPurgeRequest? LastPurgeRequest { get; private set; }

        public Task RunAsync(Configuration cfg, IProgress<double>? progress, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task RunRotationAsync(Configuration cfg, IProgress<double>? progress, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task DownloadMissingPoolsAsync(Configuration cfg, IProgress<double>? progress, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<PurgePoolsResult> PurgeAsync(PoolPurgeRequest request, CancellationToken cancellationToken)
        {
            LastPurgeRequest = request;
            return Task.FromResult(new PurgePoolsResult());
        }
    }

    private static string FindRepoFile(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            var path = Path.Combine(directory.FullName, "src", "Jellyfin.Plugin.PosterRotator", fileName);
            if (File.Exists(path))
                return path;

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Unable to locate " + fileName + " from the test output directory.");
    }

    private sealed class ProgressRecorder : IProgress<double>
    {
        public double LastValue { get; private set; }

        public void Report(double value) => LastValue = value;
    }
}
