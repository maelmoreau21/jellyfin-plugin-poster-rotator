using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using MediaBrowser.Common.Api;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.PosterRotator.Api;

/// <summary>
/// API controller for pool management actions.
/// </summary>
[ApiController]
[Route("PosterRotator")]
[Authorize(Policy = Policies.RequiresElevation)]
public class PurgeController : ControllerBase
{
    private readonly PosterRotatorService _service;

    public PurgeController(PosterRotatorService service)
    {
        _service = service;
    }

    /// <summary>
    /// Delete ALL .poster_pool directories across all libraries.
    /// </summary>
    [HttpPost("PurgeAllPools")]
    public async Task<ActionResult<PurgePoolsResult>> PurgeAllPools(CancellationToken cancellationToken)
    {
        var result = await _service.PurgeAllPoolsAsync(cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }
}
