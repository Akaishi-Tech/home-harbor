using HomeHarbor.Api.Auth;
using HomeHarbor.Api.Data;
using HomeHarbor.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HomeHarbor.Api.Controllers;

[ApiController]
[Authorize(Policy = AuthorizationPolicies.FamilyMember)]
[Route("api/recovery")]
public sealed class RecoveryController(HomeHarborDbContext db, IFamilyResolver families) : ControllerBase
{
    [HttpGet("drills")]
    public async Task<IActionResult> List([FromQuery] Guid? familyId, CancellationToken cancellationToken)
    {
        var resolved = await families.ResolveAsync(familyId, cancellationToken);
        var mayManage = User.IsInRole(HomeHarbor.Core.Identity.FamilyRoles.Owner) ||
            User.IsInRole(HomeHarbor.Core.Identity.FamilyRoles.Admin);
        return resolved is null
            ? BadRequest(new { error = "Create a family space first." })
            : Ok(await db.RecoveryDrills
            .AsNoTracking()
            .Where(d => d.FamilyId == resolved.Value)
            .OrderByDescending(d => d.StartedAt)
            .Select(d => new
            {
                d.Id,
                d.FamilyId,
                d.BackupTargetId,
                d.State,
                result = mayManage ? d.Result : null,
                d.StartedAt,
                d.FinishedAt
            })
            .ToListAsync(cancellationToken));
    }

    [HttpPost("drills")]
    [Authorize(Policy = AuthorizationPolicies.FamilyAdmin)]
    public IActionResult Start([FromBody] StartRecoveryDrillRequest request)
        => StatusCode(StatusCodes.Status501NotImplemented, new
        {
            error = "Recovery drills are unavailable because no isolated restore runner is installed."
        });

    public sealed record StartRecoveryDrillRequest(Guid? FamilyId, Guid? BackupTargetId);
}
