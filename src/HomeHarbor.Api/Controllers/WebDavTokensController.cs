using HomeHarbor.Api.Auth;
using HomeHarbor.Api.Data;
using HomeHarbor.Api.Services;
using HomeHarbor.Core.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HomeHarbor.Api.Controllers;

[ApiController]
[Route("api/webdav-tokens")]
public sealed class WebDavTokensController(
    HomeHarborDbContext db,
    IFamilyResolver families,
    ITokenGenerator tokenGenerator) : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = AuthorizationPolicies.FamilyAdmin)]
    public async Task<IActionResult> List([FromQuery] Guid? familyId, CancellationToken cancellationToken)
    {
        var resolved = await families.ResolveAsync(familyId, cancellationToken);
        if (resolved is null) return BadRequest(new { error = "Create a family space first." });

        var tokens = await db.WebDavTokens
            .AsNoTracking()
            .Where(t => t.FamilyId == resolved.Value)
            .OrderByDescending(t => t.CreatedAt)
            .Select(t => new
            {
                t.Id,
                t.FamilyId,
                t.DeviceId,
                t.Username,
                t.Scope,
                t.Description,
                t.CreatedAt,
                t.LastUsedAt
            })
            .ToListAsync(cancellationToken);

        return Ok(tokens);
    }

    [HttpPost]
    [Authorize(Policy = AuthorizationPolicies.FamilyAdmin)]
    public async Task<IActionResult> Create([FromBody] CreateWebDavTokenRequest request, CancellationToken cancellationToken)
    {
        var familyId = await families.ResolveAsync(request.FamilyId, cancellationToken);
        if (familyId is null) return BadRequest(new { error = "Create a family space before issuing WebDAV tokens." });

        var username = string.IsNullOrWhiteSpace(request.Username)
            ? tokenGenerator.GenerateUsername("dav")
            : request.Username.Trim();
        if (!IsValidUsername(username))
            return BadRequest(new { error = "Username must contain 1 to 64 ASCII letters, digits, dots, underscores, or hyphens." });
        if (request.Description?.Trim().Length > 160)
            return BadRequest(new { error = "Description must not exceed 160 characters." });
        if (!Enum.IsDefined(request.Scope))
            return BadRequest(new { error = "WebDAV scope is invalid." });
        if (request.DeviceId is { } deviceId && !await db.Devices.AsNoTracking().AnyAsync(
            device => device.Id == deviceId && device.FamilyId == familyId.Value,
            cancellationToken))
        {
            return BadRequest(new { error = "Device does not belong to this family." });
        }
        if (await db.WebDavTokens.AsNoTracking().AnyAsync(t => t.Username == username, cancellationToken))
            return Conflict(new { error = "Username already exists." });

        var plaintext = tokenGenerator.GenerateSecret();
        var token = new WebDavTokenEntity
        {
            Id = Guid.NewGuid(),
            FamilyId = familyId.Value,
            DeviceId = request.DeviceId,
            Username = username,
            TokenHash = BCrypt.Net.BCrypt.HashPassword(plaintext),
            Scope = request.Scope,
            Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim(),
            CreatedAt = DateTimeOffset.UtcNow
        };

        _ = db.WebDavTokens.Add(token);
        _ = await db.SaveChangesAsync(cancellationToken);

        return Ok(new
        {
            token.Id,
            token.FamilyId,
            token.DeviceId,
            token.Username,
            token = plaintext,
            token.Scope,
            token.Description,
            token.CreatedAt
        });
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = AuthorizationPolicies.FamilyAdmin)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var token = await db.WebDavTokens.FindAsync([id], cancellationToken);
        if (token is null) return NotFound();
        await families.RequireAccessAsync(token.FamilyId, cancellationToken);
        _ = db.WebDavTokens.Remove(token);
        _ = await db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    public sealed record CreateWebDavTokenRequest(
        Guid? FamilyId,
        Guid? DeviceId,
        string? Username,
        WebDavTokenScope Scope,
        string? Description);

    private static bool IsValidUsername(string username)
        => username.Length is > 0 and <= 64 &&
            username.All(character =>
                character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '_' or '-');
}
