using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PosterRotator;

public class DownloadMissingPoolsTask : IScheduledTask
{
    private readonly IPosterRotatorService _service;
    private readonly ILogger<DownloadMissingPoolsTask> _logger;

    public DownloadMissingPoolsTask(IPosterRotatorService service, ILogger<DownloadMissingPoolsTask> logger)
    {
        _service = service;
        _logger = logger;
    }

    public string Name => "Download missing pools";
    public string Description => "Downloads images for missing or incomplete poster pools without rotating posters.";
    public string Category => "Poster Rotator";
    public string Key => "PosterRotator.DownloadMissingPoolsTask";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        _logger.LogInformation("PosterRotator: missing-pool download task started at {Started}", started);

        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            _logger.LogError("PosterRotator: Plugin.Instance is null; aborting missing-pool download.");
            return;
        }

        var cfg = plugin.Configuration ?? new Configuration();

        try
        {
            await _service.DownloadMissingPoolsAsync(cfg, progress, cancellationToken).ConfigureAwait(false);
            var ended = DateTimeOffset.UtcNow;
            _logger.LogInformation("PosterRotator: missing-pool download task completed in {Duration}", ended - started);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("PosterRotator: missing-pool download task was cancelled.");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PosterRotator: missing-pool download task failed.");
            throw;
        }
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => new[]
    {
        new TaskTriggerInfo { Type = TaskTriggerInfoType.DailyTrigger, TimeOfDayTicks = TimeSpan.FromHours(2).Ticks }
    };
}
