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
    ISetupPairingService pairings) : ControllerBase
{
    [HttpGet]
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
    [Authorize(Policy = AuthorizationPolicies.FamilyAdmin)]
    public async Task<IActionResult> Register([FromBody] RegisterDeviceRequest request, CancellationToken cancellationToken)
    {
        if (!pairings.IsValid(request.PairingCode))
            return BadRequest(new { error = "Pairing code is expired or invalid. Scan the setup QR code again." });

        var resolved = await families.ResolveAsync(request.FamilyId, cancellationToken);
        if (resolved is null) return BadRequest(new { error = "Create a family space first." });

        var now = DateTimeOffset.UtcNow;
        var device = new DeviceEntity
        {
            Id = Guid.NewGuid(),
            FamilyId = resolved.Value,
            DisplayName = string.IsNullOrWhiteSpace(request.DisplayName) ? "HomeHarbor device" : request.DisplayName.Trim(),
            Kind = string.IsNullOrWhiteSpace(request.Kind) ? "mobile" : request.Kind.Trim().ToLowerInvariant(),
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
                FamilyId = resolved.Value,
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
        pairings.Consume(request.PairingCode);

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
