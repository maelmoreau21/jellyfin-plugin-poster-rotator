using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.PosterRotator;

public class PosterRotationTask : IScheduledTask
{
    private readonly IPosterRotatorService _service;
    private readonly ILogger<PosterRotationTask> _logger;
    private readonly IPosterRotatorLocalization _localization;

    public PosterRotationTask(
        IPosterRotatorService service,
        ILogger<PosterRotationTask> logger,
        IPosterRotatorLocalization localization)
    {
        _service = service;
        _logger = logger;
        _localization = localization;
    }

    public string Name => _localization.Translate("Task.RotatePools.Name");
    public string Description => _localization.Translate("Task.RotatePools.Description");
    public string Category => "Poster Rotator";
    public string Key => "PosterRotator.RotatePostersTask";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        _logger.LogInformation("PosterRotator: scheduled task started at {Started}", started);

        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            _logger.LogError("PosterRotator: Plugin.Instance is null; aborting.");
            return;
        }

        var cfg = plugin.Configuration ?? new Configuration();

        try
        {
            await _service.RunRotationAsync(cfg, progress, cancellationToken).ConfigureAwait(false);
            var ended = DateTimeOffset.UtcNow;
            _logger.LogInformation("PosterRotator: scheduled task completed in {Duration}", ended - started);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("PosterRotator: scheduled task was cancelled.");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PosterRotator: scheduled task failed.");
            throw;
        }
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => new[]
    {
        new TaskTriggerInfo { Type = TaskTriggerInfoType.DailyTrigger, TimeOfDayTicks = TimeSpan.FromHours(3).Ticks }
    };
}
