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

    public PosterRotationTask(IPosterRotatorService service, ILogger<PosterRotationTask> logger)
    {
        _service = service;
        _logger = logger;
    }

    public string Name => "Rotate pools";
    public string Description => "Fills poster pools when needed, then rotates eligible posters from the local pools.";
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
            await _service.RunAsync(cfg, progress, cancellationToken).ConfigureAwait(false);
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
