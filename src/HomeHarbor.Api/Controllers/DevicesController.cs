using HomeHarbor.Api.Auth;
using HomeHarbor.Api.Data;
using HomeHarbor.Api.Services;
using HomeHarbor.Core.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HomeHarbor.Api.Controllers;

[ApiController]
[Route("api/devices")]
public sealed class DevicesController(
    HomeHarborDbContext db,
    IFamilyResolver families,
    ITokenGenerator tokenGenerator,
    ISetupPairingService pairings,
    AuthenticationFailureThrottle throttle) : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = AuthorizationPolicies.FamilyAdmin)]
    public async Task<IActionResult> List([FromQuery] Guid? familyId, CancellationToken cancellationToken)
    {
        var resolved = await families.ResolveAsync(familyId, cancellationToken);
        if (resolved is null) return BadRequest(new { error = "Create a family space first." });

        var devices = await db.Devices
            .AsNoTracking()
            .Where(d => d.FamilyId == resolved.Value)
            .OrderByDescending(d => d.LastSeenAt ?? d.CreatedAt)
            .Select(d => new { d.Id, d.FamilyId, d.DisplayName, d.Kind, d.CreatedAt, d.LastSeenAt })
            .ToListAsync(cancellationToken);
        return Ok(devices);
    }

    [HttpPost]
    [AllowAnonymous]
    public async Task<IActionResult> Register([FromBody] RegisterDeviceRequest request, CancellationToken cancellationToken)
    {
        var displayName = string.IsNullOrWhiteSpace(request.DisplayName) ? "HomeHarbor device" : request.DisplayName.Trim();
        var kind = string.IsNullOrWhiteSpace(request.Kind) ? "mobile" : request.Kind.Trim().ToLowerInvariant();
        if (displayName.Length > 96 || kind.Length > 32 || kind is not ("browser" or "mobile" or "desktop"))
            return BadRequest(new { error = "Device name or kind is invalid." });
        if (request.Scope is { } requestedScope && !Enum.IsDefined(requestedScope))
            return BadRequest(new { error = "WebDAV scope is invalid." });

        var clientIdentity = AuthenticationClientIdentity.Resolve(HttpContext);
        var accountIdentity = request.PairingCode?.Trim() ?? string.Empty;
        var clientAllowed = throttle.TryAcquire("device-pair-client", clientIdentity, out var clientRetryAfter);
        var accountAllowed = throttle.TryAcquire("device-pair", accountIdentity, out var accountRetryAfter);
        if (!clientAllowed || !accountAllowed)
        {
            var retryAfter = clientRetryAfter > accountRetryAfter ? clientRetryAfter : accountRetryAfter;
            Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
            return StatusCode(StatusCodes.Status429TooManyRequests, new { error = "Too many failed pairing attempts. Try again later." });
        }
        if (!pairings.TryConsumeDeviceCode(request.PairingCode, out var ticket) || ticket is null)
        {
            throttle.RecordFailure("device-pair-client", clientIdentity);
            throttle.RecordFailure("device-pair", accountIdentity);
            return BadRequest(new { error = "Pairing code is expired or invalid. Scan the setup QR code again." });
        }
        throttle.RecordSuccess("device-pair", accountIdentity);

        var familyId = ticket.FamilyId;
        if (!await db.FamilySpaces.AsNoTracking().AnyAsync(family => family.Id == familyId, cancellationToken))
            return BadRequest(new { error = "Pairing family no longer exists." });

        var now = DateTimeOffset.UtcNow;
        var device = new DeviceEntity
        {
            Id = Guid.NewGuid(),
            FamilyId = familyId,
            DisplayName = displayName,
            Kind = kind,
            CreatedAt = now,
            LastSeenAt = now
        };

        _ = db.Devices.Add(device);

        object? webDav = null;
        if (request.IssueWebDavToken ?? true)
        {
            var plaintext = tokenGenerator.GenerateSecret();
            var token = new WebDavTokenEntity
            {
                Id = Guid.NewGuid(),
                FamilyId = familyId,
                DeviceId = device.Id,
                Username = tokenGenerator.GenerateUsername("dav"),
                TokenHash = BCrypt.Net.BCrypt.HashPassword(plaintext),
                Scope = request.Scope ?? WebDavTokenScope.All,
                Description = $"{device.DisplayName} sync token",
                CreatedAt = now
            };
            _ = db.WebDavTokens.Add(token);
            webDav = new { token.Id, token.Username, token = plaintext, token.Scope };
        }

        _ = await db.SaveChangesAsync(cancellationToken);

        return Ok(new
        {
            device = new { device.Id, device.FamilyId, device.DisplayName, device.Kind, device.CreatedAt, device.LastSeenAt },
            sync = new
            {
                scopes = new[] { "files", "photos", "backups" },
                endpoint = "/api/sync/states"
            },
            webDav
        });
    }

    [HttpPost("{id:guid}/heartbeat")]
    [Authorize(Policy = AuthorizationPolicies.FamilyAdmin)]
    public async Task<IActionResult> Heartbeat(Guid id, CancellationToken cancellationToken)
    {
        var device = await db.Devices.FindAsync([id], cancellationToken);
        if (device is null) return NotFound();
        await families.RequireAccessAsync(device.FamilyId, cancellationToken);

        device.LastSeenAt = DateTimeOffset.UtcNow;
        _ = await db.SaveChangesAsync(cancellationToken);
        return Ok(new { device.Id, device.LastSeenAt });
    }

    public sealed record RegisterDeviceRequest(
        Guid? FamilyId,
        string? DisplayName,
        string? Kind,
        string? PairingCode,
        bool? IssueWebDavToken,
        WebDavTokenScope? Scope);
}
