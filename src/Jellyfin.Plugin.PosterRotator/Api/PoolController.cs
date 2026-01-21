namespace Jellyfin.Plugin.PosterRotator.Api;

using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PosterRotator.Models;
using Jellyfin.Plugin.PosterRotator.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

/// <summary>
/// API REST pour la gestion des pools d'images du Poster Rotator.
/// </summary>
[ApiController]
[Route("PosterRotator")]
[Authorize(Policy = "RequiresElevation")]
[Produces(MediaTypeNames.Application.Json)]
public class PoolController : ControllerBase
{
    private readonly PoolService _poolService;
    private readonly ILogger<PoolController> _log;

    public PoolController(PoolService poolService, ILogger<PoolController> log)
    {
        _poolService = poolService;
        _log = log;
    }

    /// <summary>
    /// Récupère les statistiques globales des pools.
    /// </summary>
    [HttpGet("Stats")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<PoolStatistics>> GetStatistics(CancellationToken ct)
    {
        _log.LogDebug("PoolController: Getting statistics");
        var stats = await _poolService.GetStatisticsAsync(ct).ConfigureAwait(false);
        return Ok(stats);
    }

    /// <summary>
    /// Récupère la liste de tous les items ayant un pool.
    /// </summary>
    [HttpGet("Items")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<List<PoolInfo>>> GetAllItems(CancellationToken ct)
    {
        _log.LogDebug("PoolController: Getting all items with pools");
        var pools = await _poolService.GetAllPoolsAsync(ct).ConfigureAwait(false);
        return Ok(pools);
    }

    /// <summary>
    /// Récupère les détails du pool d'un item spécifique.
    /// </summary>
    [HttpGet("Pool/{itemId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PoolInfo>> GetPool([FromRoute] Guid itemId, CancellationToken ct)
    {
        _log.LogDebug("PoolController: Getting pool for item {ItemId}", itemId);
        var pool = await _poolService.GetPoolForItemAsync(itemId, ct).ConfigureAwait(false);
        
        if (pool == null)
        {
            return NotFound(new { message = "Pool not found for this item" });
        }

        return Ok(pool);
    }

    /// <summary>
    /// Récupère une image du pool.
    /// </summary>
    [HttpGet("Pool/{itemId}/Image/{fileName}")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetPoolImage(
        [FromRoute] Guid itemId, 
        [FromRoute] string fileName, 
        CancellationToken ct)
    {
        var (data, contentType) = await _poolService.GetPoolImageAsync(itemId, fileName, ct).ConfigureAwait(false);
        
        if (data == null || contentType == null)
        {
            return NotFound();
        }

        return File(data, contentType);
    }

    /// <summary>
    /// Upload une nouvelle image dans le pool d'un item.
    /// </summary>
    [HttpPost("Pool/{itemId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> UploadImage(
        [FromRoute] Guid itemId,
        [Required] IFormFile file,
        CancellationToken ct)
    {
        if (file == null || file.Length == 0)
        {
            return BadRequest(new { message = "No file provided" });
        }

        // Vérifier le type de fichier
        var allowedTypes = new[] { "image/jpeg", "image/png", "image/webp", "image/gif" };
        if (!Array.Exists(allowedTypes, t => t.Equals(file.ContentType, StringComparison.OrdinalIgnoreCase)))
        {
            return BadRequest(new { message = "Invalid file type. Allowed: JPEG, PNG, WebP, GIF" });
        }

        _log.LogInformation("PoolController: Uploading image {FileName} for item {ItemId}", file.FileName, itemId);

        using var stream = file.OpenReadStream();
        var success = await _poolService.AddImageToPoolAsync(itemId, stream, file.FileName, ct).ConfigureAwait(false);

        if (!success)
        {
            return NotFound(new { message = "Item not found or upload failed" });
        }

        return Ok(new { message = "Image uploaded successfully" });
    }

    /// <summary>
    /// Supprime une image du pool d'un item.
    /// </summary>
    [HttpDelete("Pool/{itemId}/{fileName}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> DeleteImage(
        [FromRoute] Guid itemId,
        [FromRoute] string fileName,
        CancellationToken ct)
    {
        _log.LogInformation("PoolController: Deleting image {FileName} from pool for item {ItemId}", fileName, itemId);

        var success = await _poolService.DeleteImageFromPoolAsync(itemId, fileName, ct).ConfigureAwait(false);

        if (!success)
        {
            return NotFound(new { message = "Image not found or deletion failed" });
        }

        return Ok(new { message = "Image deleted successfully" });
    }

    /// <summary>
    /// Réordonne les images dans un pool.
    /// </summary>
    [HttpPost("Pool/{itemId}/Reorder")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> ReorderPool(
        [FromRoute] Guid itemId,
        [FromBody] ReorderRequest request,
        CancellationToken ct)
    {
        if (request?.FileNames == null || request.FileNames.Count == 0)
        {
            return BadRequest(new { message = "FileNames list is required" });
        }

        _log.LogInformation("PoolController: Reordering pool for item {ItemId}", itemId);

        var success = await _poolService.ReorderPoolAsync(itemId, request.FileNames, ct).ConfigureAwait(false);

        if (!success)
        {
            return NotFound(new { message = "Item not found or reorder failed" });
        }

        return Ok(new { message = "Pool reordered successfully" });
    }

    /// <summary>
    /// Nettoie les pools orphelins (médias supprimés).
    /// </summary>
    [HttpPost("Cleanup")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult> CleanupOrphanedPools(CancellationToken ct)
    {
        _log.LogInformation("PoolController: Starting cleanup of orphaned pools");

        var count = await _poolService.CleanupOrphanedPoolsAsync(ct).ConfigureAwait(false);

        return Ok(new { message = $"Deleted {count} orphaned pool(s)", deletedCount = count });
    }
}

/// <summary>
/// Requête pour réordonner les images d'un pool.
/// </summary>
public class ReorderRequest
{
    /// <summary>
    /// Liste ordonnée des noms de fichiers.
    /// </summary>
    public List<string> FileNames { get; set; } = new();
}
