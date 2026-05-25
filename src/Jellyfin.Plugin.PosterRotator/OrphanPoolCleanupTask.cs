using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PosterRotator;

public class OrphanPoolCleanupTask : IScheduledTask
{
    private readonly IPosterRotatorService _service;
    private readonly ILogger<OrphanPoolCleanupTask> _logger;

    public OrphanPoolCleanupTask(IPosterRotatorService service, ILogger<OrphanPoolCleanupTask> logger)
    {
        _service = service;
        _logger = logger;
    }

    public string Name => "Nettoyage pools orphelins";
    public string Description => "Deletes PluginData pools whose Jellyfin media no longer exists.";
    public string Category => "Poster Rotator";
    public string Key => "PosterRotator.CleanupOrphanPools";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        _logger.LogInformation("PosterRotator: orphan pool cleanup started.");
        var result = await _service.PurgeAsync(
            new PoolPurgeRequest { Scope = "orphans" },
            cancellationToken).ConfigureAwait(false);

        progress.Report(100);
        _logger.LogInformation(
            "PosterRotator: orphan pool cleanup completed - {Deleted} deleted, {Skipped} skipped, {Failed} failed.",
            result.DeletedCount,
            result.SkippedCount,
            result.FailedCount);
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => new[]
    {
        new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.WeeklyTrigger,
            DayOfWeek = DayOfWeek.Sunday,
            TimeOfDayTicks = TimeSpan.FromHours(4).Ticks
        }
    };
}
