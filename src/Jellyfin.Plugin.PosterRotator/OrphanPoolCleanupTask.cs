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
    private readonly IPosterRotatorLocalization _localization;

    public OrphanPoolCleanupTask(
        IPosterRotatorService service,
        ILogger<OrphanPoolCleanupTask> logger,
        IPosterRotatorLocalization localization)
    {
        _service = service;
        _logger = logger;
        _localization = localization;
    }

    public string Name => _localization.Translate("Task.CleanOrphanPools.Name");
    public string Description => _localization.Translate("Task.CleanOrphanPools.Description");
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
