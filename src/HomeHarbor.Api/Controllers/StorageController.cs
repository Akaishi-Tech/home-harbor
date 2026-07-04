using HomeHarbor.Api.Auth;
using HomeHarbor.Api.Data;
using HomeHarbor.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HomeHarbor.Api.Controllers;

[ApiController]
[Route("api/storage")]
public sealed class StorageController(
    HomeHarborDbContext db,
    IFamilyResolver families,
    IStorageHealthService health) : ControllerBase
{
    [Authorize(Policy = AuthorizationPolicies.Automation)]
    [HttpPost("health/check")]
    public async Task<IActionResult> Check([FromBody] StorageHealthRequest request, CancellationToken cancellationToken)
    {
        var resolved = await families.ResolveAsync(request.FamilyId, cancellationToken);
        if (resolved is null) return BadRequest(new { error = "Create a family space first." });

        var snapshot = health.Check(resolved.Value);
        _ = db.StorageHealthSnapshots.Add(snapshot);
        _ = await db.SaveChangesAsync(cancellationToken);
        return Ok(snapshot);
    }

    [HttpGet("health")]
    public async Task<IActionResult> History([FromQuery] Guid? familyId, CancellationToken cancellationToken)
    {
        var resolved = await families.ResolveAsync(familyId, cancellationToken);
        return resolved is null
            ? BadRequest(new { error = "Create a family space first." })
            : Ok(await db.StorageHealthSnapshots
            .AsNoTracking()
            .Where(s => s.FamilyId == resolved.Value)
            .OrderByDescending(s => s.CheckedAt)
            .Take(50)
            .ToListAsync(cancellationToken));
    }

    public sealed record StorageHealthRequest(Guid? FamilyId);
}
