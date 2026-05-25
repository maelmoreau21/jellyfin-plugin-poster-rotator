using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using MediaBrowser.Common.Api;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PosterRotator.Helpers;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Http;

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
    private readonly IImageProcessor _imageProcessor;

    public PurgeController(PosterRotatorService service, IImageProcessor imageProcessor)
    {
        _service = service;
        _imageProcessor = imageProcessor;
    }

    [HttpGet("Diagnostics")]
    public async Task<ActionResult<PoolDiagnostics>> GetDiagnostics(CancellationToken cancellationToken)
    {
        var result = await _service.GetDiagnosticsAsync(cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    [HttpGet("Pools")]
    public async Task<ActionResult<PoolListResponse>> GetPools(
        [FromQuery] string? library,
        [FromQuery] string? query,
        [FromQuery] string? type,
        [FromQuery] bool? hasErrors,
        [FromQuery] bool? isEmpty,
        [FromQuery] int start = 0,
        [FromQuery] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var result = await _service.ListPoolsAsync(
            new PoolListQuery
            {
                Library = library,
                Query = query,
                Type = type,
                HasErrors = hasErrors,
                IsEmpty = isEmpty,
                Start = start,
                Limit = limit
            },
            cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    [HttpPost("Pools/RebuildIndex")]
    public async Task<ActionResult<PoolRebuildIndexResult>> RebuildPoolIndex(CancellationToken cancellationToken)
    {
        var result = await _service.RebuildPoolIndexAsync(cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    [HttpGet("Pools/{itemId:guid}")]
    public async Task<ActionResult<PoolMetadata>> GetPool(Guid itemId, CancellationToken cancellationToken)
    {
        var pool = await _service.GetPoolAsync(itemId, cancellationToken).ConfigureAwait(false);
        return pool == null ? NotFound() : Ok(pool);
    }

    [HttpGet("Pools/{itemId:guid}/Images/{*fileName}")]
    public async Task<IActionResult> GetPoolImage(
        Guid itemId,
        string fileName,
        [FromQuery] bool preview = false,
        [FromQuery] int maxWidth = 320,
        [FromQuery] int maxHeight = 480,
        [FromQuery] int quality = 80,
        CancellationToken cancellationToken = default)
    {
        try
        {
            fileName = Uri.UnescapeDataString(fileName ?? string.Empty);
            var image = await _service.GetPoolImageAsync(itemId, fileName, cancellationToken).ConfigureAwait(false);
            var file = preview
                ? await GetPreviewImageAsync(image, maxWidth, maxHeight, quality).ConfigureAwait(false)
                : image;

            Response.Headers["Cache-Control"] = preview ? "private, max-age=3600" : "private, max-age=60";
            return File(System.IO.File.OpenRead(file.Path), file.ContentType);
        }
        catch (FileNotFoundException)
        {
            return NotFound();
        }
        catch (InvalidDataException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (Exception) when (preview)
        {
            return BadRequest("Preview unavailable.");
        }
    }

    private async Task<PoolImageFile> GetPreviewImageAsync(PoolImageFile image, int maxWidth, int maxHeight, int quality)
    {
        maxWidth = Math.Clamp(maxWidth <= 0 ? 320 : maxWidth, 64, 720);
        maxHeight = Math.Clamp(maxHeight <= 0 ? 480 : maxHeight, 96, 1280);
        quality = Math.Clamp(quality <= 0 ? 80 : quality, 40, 90);

        var originalSize = PluginHelpers.GetImageDimensions(image.Path);
        var originalTooLarge = originalSize.Width > maxWidth || originalSize.Height > maxHeight;
        var fileInfo = new FileInfo(image.Path);
        var processed = await _imageProcessor.ProcessImage(new ImageProcessingOptions
        {
            Image = new ItemImageInfo
            {
                Path = image.Path,
                Type = ImageType.Primary,
                DateModified = fileInfo.Exists ? fileInfo.LastWriteTimeUtc : DateTime.UtcNow
            },
            MaxWidth = maxWidth,
            MaxHeight = maxHeight,
            Quality = quality,
            SupportedOutputFormats = _imageProcessor.GetSupportedImageOutputFormats()
        }).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(processed.Path) || !System.IO.File.Exists(processed.Path))
            throw new InvalidDataException("Preview unavailable.");

        if (originalTooLarge && Path.GetFullPath(processed.Path).Equals(Path.GetFullPath(image.Path), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Preview unavailable.");

        var previewSize = PluginHelpers.GetImageDimensions(processed.Path);
        if (previewSize.Width > maxWidth || previewSize.Height > maxHeight)
            throw new InvalidDataException("Preview unavailable.");

        return new PoolImageFile(processed.Path, string.IsNullOrWhiteSpace(processed.MimeType) ? image.ContentType : processed.MimeType);
    }

    [HttpPost("Pools/{itemId:guid}/RotateNow")]
    public async Task<ActionResult<PoolOperationResult>> RotatePoolNow(Guid itemId, CancellationToken cancellationToken)
    {
        var result = await _service.RotatePoolNowAsync(itemId, cancellationToken).ConfigureAwait(false);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    [HttpPost("Libraries/{libraryName}/RotateNow")]
    public async Task<ActionResult<PoolOperationResult>> RotateLibraryNow(string libraryName, CancellationToken cancellationToken)
    {
        var result = await _service.RotateLibraryNowAsync(libraryName, cancellationToken).ConfigureAwait(false);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    [HttpPost("Pools/{itemId:guid}/Images")]
    [RequestSizeLimit(209715200)]
    public async Task<ActionResult<PoolImageMetadata>> UploadPoolImage(
        Guid itemId,
        [FromForm] IFormFile file,
        CancellationToken cancellationToken)
    {
        if (file == null || file.Length == 0)
            return BadRequest("Aucun fichier recu.");

        var cfg = Plugin.Instance?.Configuration ?? new Configuration();
        var maxUploadBytes = Math.Clamp(cfg.MaxDownloadMegabytes, 1, 200) * 1024L * 1024L;
        if (file.Length > maxUploadBytes)
            return BadRequest("Image trop volumineuse.");

        try
        {
            await using var stream = file.OpenReadStream();
            var result = await _service.ImportPoolImageAsync(itemId, stream, file.FileName, cancellationToken).ConfigureAwait(false);
            return Ok(result);
        }
        catch (FileNotFoundException ex)
        {
            return NotFound(ex.Message);
        }
        catch (InvalidDataException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    [HttpDelete("Pools/{itemId:guid}/Images/{*fileName}")]
    public async Task<ActionResult<PoolImageMetadata>> DeletePoolImage(
        Guid itemId,
        string fileName,
        CancellationToken cancellationToken)
    {
        try
        {
            fileName = Uri.UnescapeDataString(fileName ?? string.Empty);
            var result = await _service.DeletePoolImageAsync(itemId, fileName, cancellationToken).ConfigureAwait(false);
            return Ok(result);
        }
        catch (FileNotFoundException)
        {
            return NotFound();
        }
        catch (InvalidDataException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    [HttpPost("Purge")]
    public async Task<ActionResult<PurgePoolsResult>> Purge([FromBody] PoolPurgeRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _service.PurgeAsync(request, cancellationToken).ConfigureAwait(false);
            return Ok(result);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
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
