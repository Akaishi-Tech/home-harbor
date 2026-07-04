using HomeHarbor.Api.Data;
using HomeHarbor.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HomeHarbor.Api.Controllers;

[ApiController]
[Route("api/recovery")]
public sealed class RecoveryController(HomeHarborDbContext db, IFamilyResolver families) : ControllerBase
{
    [HttpGet("drills")]
    public async Task<IActionResult> List([FromQuery] Guid? familyId, CancellationToken cancellationToken)
    {
        var resolved = await families.ResolveAsync(familyId, cancellationToken);
        return resolved is null
            ? BadRequest(new { error = "Create a family space first." })
            : Ok(await db.RecoveryDrills
            .AsNoTracking()
            .Where(d => d.FamilyId == resolved.Value)
            .OrderByDescending(d => d.StartedAt)
            .ToListAsync(cancellationToken));
    }

    [HttpPost("drills")]
    public async Task<IActionResult> Start([FromBody] StartRecoveryDrillRequest request, CancellationToken cancellationToken)
    {
        var resolved = await families.ResolveAsync(request.FamilyId, cancellationToken);
        if (resolved is null) return BadRequest(new { error = "Create a family space first." });

        BackupTargetEntity? target = null;
        if (request.BackupTargetId is { } targetId)
        {
            target = await db.BackupTargets.AsNoTracking().FirstOrDefaultAsync(t => t.Id == targetId && t.FamilyId == resolved.Value, cancellationToken);
            if (target is null) return NotFound(new { error = "Backup target not found." });
        }

        var drill = new RecoveryDrillEntity
        {
            Id = Guid.NewGuid(),
            FamilyId = resolved.Value,
            BackupTargetId = target?.Id,
            State = "completed",
            Result = target is null
                ? "Local restore drill recorded. No external target was selected."
                : $"Restore drill command: restic -r {target.RepositoryUri} restore latest --target /tmp/homeharbor-restore-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}",
            StartedAt = DateTimeOffset.UtcNow,
            FinishedAt = DateTimeOffset.UtcNow
        };

        _ = db.RecoveryDrills.Add(drill);
        _ = await db.SaveChangesAsync(cancellationToken);
        return Ok(drill);
    }

    public sealed record StartRecoveryDrillRequest(Guid? FamilyId, Guid? BackupTargetId);
}
