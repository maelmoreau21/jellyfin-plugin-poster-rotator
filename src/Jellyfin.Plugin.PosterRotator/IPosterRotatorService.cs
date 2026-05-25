using System;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.PosterRotator;

public interface IPosterRotatorService
{
    Task RunAsync(Configuration cfg, IProgress<double>? progress, CancellationToken cancellationToken);

    Task<PurgePoolsResult> PurgeAsync(PoolPurgeRequest request, CancellationToken cancellationToken);
}
