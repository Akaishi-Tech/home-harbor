using System.Globalization;
using HomeHarbor.Api.Auth;
using HomeHarbor.Api.Data;
using HomeHarbor.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HomeHarbor.Api.Controllers;

[ApiController]
[Route("api/sync")]
public sealed class SyncController(
    HomeHarborDbContext db,
    IFamilyResolver families,
    IAuthorizationService authorization) : ControllerBase
{
    [HttpGet("states")]
    [Authorize(Policy = AuthorizationPolicies.FamilyAdmin)]
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
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme + "," + BasicAuthenticationHandler.SchemeName)]
    public async Task<IActionResult> Upsert([FromBody] UpsertSyncStateRequest request, CancellationToken cancellationToken)
    {
        var scope = string.IsNullOrWhiteSpace(request.Scope) ? "files" : request.Scope.Trim();
        if (scope.Length > 64 || scope is not ("files" or "photos" or "backups"))
            return BadRequest(new { error = "scope must be files, photos, or backups." });
        if (request.Cursor?.Length > 2048)
            return BadRequest(new { error = "cursor must not exceed 2048 characters." });

        Guid familyId;
        Guid deviceId;
        var isWebDavDevice = User.Identities.Any(
            identity => identity.IsAuthenticated && identity.AuthenticationType == BasicAuthenticationHandler.SchemeName);
        if (isWebDavDevice)
        {
            var webDavIdentity = WebDavIdentity.FromPrincipal(User);
            if (webDavIdentity.DeviceId is not { } tokenDeviceId)
                return Forbid();
            if (request.FamilyId is { } requestedFamily && requestedFamily != webDavIdentity.FamilyId)
                return Forbid();
            if (request.DeviceId != Guid.Empty && request.DeviceId != tokenDeviceId)
                return Forbid();
            var requestedArea = scope switch
            {
                "files" => HomeHarbor.Core.Storage.StorageArea.Files,
                "photos" => HomeHarbor.Core.Storage.StorageArea.Photos,
                _ => HomeHarbor.Core.Storage.StorageArea.Backups
            };
            if (!webDavIdentity.CanAccess(requestedArea)) return Forbid();
            familyId = webDavIdentity.FamilyId;
            deviceId = tokenDeviceId;
        }
        else
        {
            if (!(await authorization.AuthorizeAsync(User, null, AuthorizationPolicies.FamilyAdmin)).Succeeded)
                return Forbid();
            var resolved = await families.ResolveAsync(request.FamilyId, cancellationToken);
            if (resolved is null) return BadRequest(new { error = "Create a family space first." });
            if (request.DeviceId == Guid.Empty) return BadRequest(new { error = "deviceId is required." });
            familyId = resolved.Value;
            deviceId = request.DeviceId;
        }

        var device = await db.Devices.FirstOrDefaultAsync(
            candidate => candidate.Id == deviceId && candidate.FamilyId == familyId,
            cancellationToken);
        if (device is null)
        {
            return NotFound(new { error = "Device not found." });
        }

        var now = DateTimeOffset.UtcNow;
        if (isWebDavDevice && ShouldRefreshDeviceLastSeen(device.LastSeenAt, now))
        {
            device.LastSeenAt = now;
        }

        var state = await db.SyncStates.FirstOrDefaultAsync(
            s => s.FamilyId == familyId && s.DeviceId == deviceId && s.Scope == scope,
            cancellationToken);
        if (state is null)
        {
            state = new SyncStateEntity
            {
                Id = Guid.NewGuid(),
                FamilyId = familyId,
                DeviceId = deviceId,
                Scope = scope
            };
            _ = db.SyncStates.Add(state);
        }

        state.Cursor = string.IsNullOrWhiteSpace(request.Cursor)
            ? now.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)
            : request.Cursor;
        state.UpdatedAt = now;
        _ = await db.SaveChangesAsync(cancellationToken);
        return Ok(state);
    }

    internal static bool ShouldRefreshDeviceLastSeen(DateTimeOffset? lastSeenAt, DateTimeOffset now)
        => lastSeenAt is null || now - lastSeenAt.Value >= TimeSpan.FromMinutes(5);

    public sealed record UpsertSyncStateRequest(Guid? FamilyId, Guid DeviceId, string? Scope, string? Cursor);
}
