using HomeHarbor.Api.Data;
using HomeHarbor.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HomeHarbor.Api.Controllers;

[ApiController]
[Route("api/media")]
public sealed class MediaController(HomeHarborDbContext db, IFamilyResolver families, IMediaIndexer indexer) : ControllerBase
{
    [HttpGet("assets")]
    public async Task<IActionResult> Assets([FromQuery] Guid? familyId, [FromQuery] string? type, CancellationToken cancellationToken)
    {
        var resolved = await families.ResolveAsync(familyId, cancellationToken);
        if (resolved is null) return BadRequest(new { error = "Create a family space first." });

        var query = db.MediaAssets.AsNoTracking().Where(a => a.FamilyId == resolved.Value);
        if (!string.IsNullOrWhiteSpace(type)) query = query.Where(a => a.MediaType == type);
        var assets = await query.OrderByDescending(a => a.LastModifiedUtc).Take(500).ToListAsync(cancellationToken);
        return Ok(assets);
    }

    [HttpPost("index")]
    public async Task<IActionResult> Index([FromBody] IndexMediaRequest request, CancellationToken cancellationToken)
    {
        var resolved = await families.ResolveAsync(request.FamilyId, cancellationToken);
        if (resolved is null) return BadRequest(new { error = "Create a family space first." });

        var assets = await indexer.IndexAsync(resolved.Value, cancellationToken);
        var grouped = assets.GroupBy(a => a.MediaType).ToDictionary(g => g.Key, g => g.Count());
        return Ok(new { indexed = assets.Count, grouped });
    }

    public sealed record IndexMediaRequest(Guid? FamilyId);
}
