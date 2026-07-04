using System.Globalization;
using HomeHarbor.Api.Data;
using HomeHarbor.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HomeHarbor.Api.Controllers;

[ApiController]
[Route("api/sync")]
public sealed class SyncController(HomeHarborDbContext db, IFamilyResolver families) : ControllerBase
{
    [HttpGet("states")]
    public async Task<IActionResult> List([FromQuery] Guid? familyId, CancellationToken cancellationToken)
    {
        var resolved = await families.ResolveAsync(familyId, cancellationToken);
        return resolved is null
            ? BadRequest(new { error = "Create a family space first." })
            : Ok(await db.SyncStates
            .AsNoTracking()
            .Where(s => s.FamilyId == resolved.Value)
            .OrderByDescending(s => s.UpdatedAt)
            .ToListAsync(cancellationToken));
    }

    [HttpPost("states")]
    public async Task<IActionResult> Upsert([FromBody] UpsertSyncStateRequest request, CancellationToken cancellationToken)
    {
        var resolved = await families.ResolveAsync(request.FamilyId, cancellationToken);
        if (resolved is null) return BadRequest(new { error = "Create a family space first." });
        if (request.DeviceId == Guid.Empty) return BadRequest(new { error = "deviceId is required." });
        var scope = string.IsNullOrWhiteSpace(request.Scope) ? "files" : request.Scope.Trim();

        var state = await db.SyncStates.FirstOrDefaultAsync(
            s => s.FamilyId == resolved.Value && s.DeviceId == request.DeviceId && s.Scope == scope,
            cancellationToken);
        if (state is null)
        {
            state = new SyncStateEntity
            {
                Id = Guid.NewGuid(),
                FamilyId = resolved.Value,
                DeviceId = request.DeviceId,
                Scope = scope
            };
            _ = db.SyncStates.Add(state);
        }

        state.Cursor = string.IsNullOrWhiteSpace(request.Cursor)
            ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)
            : request.Cursor;
        state.UpdatedAt = DateTimeOffset.UtcNow;
        _ = await db.SaveChangesAsync(cancellationToken);
        return Ok(state);
    }

    public sealed record UpsertSyncStateRequest(Guid? FamilyId, Guid DeviceId, string? Scope, string? Cursor);
}
