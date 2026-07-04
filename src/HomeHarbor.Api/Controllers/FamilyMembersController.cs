using HomeHarbor.Api.Auth;
using HomeHarbor.Api.Data;
using HomeHarbor.Api.Services;
using HomeHarbor.Core.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HomeHarbor.Api.Controllers;

[ApiController]
[Route("api/family/members")]
public sealed class FamilyMembersController(HomeHarborDbContext db, IFamilyResolver families) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] Guid? familyId, CancellationToken cancellationToken)
    {
        var resolved = await families.ResolveAsync(familyId, cancellationToken);
        if (resolved is null) return BadRequest(new { error = "Create a family space first." });

        var members = await db.FamilyMembers
            .AsNoTracking()
            .Where(m => m.FamilyId == resolved.Value)
            .OrderBy(m => m.CreatedAt)
            .Select(m => new { m.Id, m.FamilyId, m.DisplayName, m.Role, m.CreatedAt })
            .ToListAsync(cancellationToken);
        return Ok(members);
    }

    [HttpGet("~/api/family/permissions")]
    public IActionResult Permissions()
        => Ok(new[]
        {
            new { role = FamilyRoles.Owner, files = "write", photos = "write", backups = "write", vault = "write", admin = true },
            new { role = FamilyRoles.Admin, files = "write", photos = "write", backups = "write", vault = "write", admin = true },
            new { role = FamilyRoles.Member, files = "write", photos = "write", backups = "read", vault = "own", admin = false },
            new { role = FamilyRoles.Child, files = "own", photos = "own", backups = "none", vault = "none", admin = false },
            new { role = FamilyRoles.Guest, files = "read", photos = "read", backups = "none", vault = "none", admin = false }
        });

    [HttpPost]
    [Authorize(Policy = AuthorizationPolicies.FamilyAdmin)]
    public async Task<IActionResult> Create([FromBody] CreateMemberRequest request, CancellationToken cancellationToken)
    {
        var resolved = await families.ResolveAsync(request.FamilyId, cancellationToken);
        if (resolved is null) return BadRequest(new { error = "Create a family space first." });

        var role = string.IsNullOrWhiteSpace(request.Role) ? FamilyRoles.Member : request.Role.Trim().ToLowerInvariant();
        if (!AllowedRoles.Contains(role)) return BadRequest(new { error = "Unsupported role." });
        var member = new FamilyMemberEntity
        {
            Id = Guid.NewGuid(),
            FamilyId = resolved.Value,
            DisplayName = string.IsNullOrWhiteSpace(request.DisplayName) ? "Family member" : request.DisplayName.Trim(),
            Role = role,
            PasswordHash = string.IsNullOrWhiteSpace(request.Password) ? null : BCrypt.Net.BCrypt.HashPassword(request.Password),
            CreatedAt = DateTimeOffset.UtcNow
        };

        _ = db.FamilyMembers.Add(member);
        _ = await db.SaveChangesAsync(cancellationToken);
        return Ok(new { member.Id, member.FamilyId, member.DisplayName, member.Role, member.CreatedAt });
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = AuthorizationPolicies.FamilyAdmin)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var member = await db.FamilyMembers.FindAsync([id], cancellationToken);
        if (member is null) return NotFound();
        await families.RequireAccessAsync(member.FamilyId, cancellationToken);
        _ = db.FamilyMembers.Remove(member);
        _ = await db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    private static readonly HashSet<string> AllowedRoles = new(StringComparer.Ordinal)
    {
        FamilyRoles.Owner,
        FamilyRoles.Admin,
        FamilyRoles.Member,
        FamilyRoles.Child,
        FamilyRoles.Guest
    };

    public sealed record CreateMemberRequest(Guid? FamilyId, string? DisplayName, string? Role, string? Password);
}
