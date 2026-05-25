using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.PosterRotator.Tests;

public sealed class ScheduledTaskTests
{
    [Fact]
    public void PosterRotationTask_UsesPosterRotatorCategoryAndStableKey()
    {
        var task = new PosterRotationTask(new FakePosterRotatorService(), NullLogger<PosterRotationTask>.Instance);

        Assert.Equal("Rotate pools", task.Name);
        Assert.Equal("Poster Rotator", task.Category);
        Assert.Equal("PosterRotator.RotatePostersTask", task.Key);
        Assert.Contains(task.GetDefaultTriggers(), trigger => trigger.Type == TaskTriggerInfoType.DailyTrigger);
    }

    [Fact]
    public async Task OrphanPoolCleanupTask_PurgesOnlyOrphans()
    {
        var service = new FakePosterRotatorService();
        var task = new OrphanPoolCleanupTask(service, NullLogger<OrphanPoolCleanupTask>.Instance);
        var progress = new ProgressRecorder();

        await task.ExecuteAsync(progress, CancellationToken.None);

        Assert.Equal("Nettoyage pools orphelins", task.Name);
        Assert.Equal("Poster Rotator", task.Category);
        Assert.Equal("PosterRotator.CleanupOrphanPools", task.Key);
        Assert.Equal("orphans", service.LastPurgeRequest?.Scope);
        Assert.Equal(100, progress.LastValue);
        Assert.Contains(task.GetDefaultTriggers(), trigger => trigger.Type == TaskTriggerInfoType.WeeklyTrigger);
    }

    private sealed class FakePosterRotatorService : IPosterRotatorService
    {
        public PoolPurgeRequest? LastPurgeRequest { get; private set; }

        public Task RunAsync(Configuration cfg, IProgress<double>? progress, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<PurgePoolsResult> PurgeAsync(PoolPurgeRequest request, CancellationToken cancellationToken)
        {
            LastPurgeRequest = request;
            return Task.FromResult(new PurgePoolsResult());
        }
    }

    private sealed class ProgressRecorder : IProgress<double>
    {
        public double LastValue { get; private set; }

        public void Report(double value) => LastValue = value;
    }
}
