using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using MediaBrowser.Common.Api;

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
    public ActionResult<PurgeResult> PurgeAllPools()
    {
        var count = _service.PurgeAllPools();
        return Ok(new PurgeResult { DeletedCount = count });
    }

    public class PurgeResult
    {
        public int DeletedCount { get; set; }
    }
}
