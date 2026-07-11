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
            new { role = FamilyRoles.Member, files = "none", photos = "none", backups = "read", vault = "none", admin = false }
        });

    [HttpPost]
    [Authorize(Policy = AuthorizationPolicies.FamilyAdmin)]
    public async Task<IActionResult> Create([FromBody] CreateMemberRequest request, CancellationToken cancellationToken)
    {
        var resolved = await families.ResolveAsync(request.FamilyId, cancellationToken);
        if (resolved is null) return BadRequest(new { error = "Create a family space first." });

        var role = string.IsNullOrWhiteSpace(request.Role) ? FamilyRoles.Member : request.Role.Trim().ToLowerInvariant();
        if (!AllowedRoles.Contains(role)) return BadRequest(new { error = "Unsupported role." });
        if (role == FamilyRoles.Owner && !User.IsInRole(FamilyRoles.Owner))
            return Forbid();
        if (string.IsNullOrWhiteSpace(request.DisplayName))
            return BadRequest(new { error = "displayName is required." });
        var displayName = request.DisplayName.Trim();
        if (displayName.Length > 96)
            return BadRequest(new { error = "displayName must not exceed 96 characters." });
        if (string.IsNullOrWhiteSpace(request.Password) || request.Password.Length < 12 || request.Password.Length > 128)
            return BadRequest(new { error = "password must contain 12 to 128 characters." });
        if (await db.FamilyMembers.AsNoTracking().AnyAsync(
            member => member.FamilyId == resolved.Value && member.DisplayName == displayName,
            cancellationToken))
        {
            return Conflict(new { error = "A family member with this display name already exists." });
        }

        var member = new FamilyMemberEntity
        {
            Id = Guid.NewGuid(),
            FamilyId = resolved.Value,
            DisplayName = displayName,
            Role = role,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password),
            CreatedAt = DateTimeOffset.UtcNow
        };

        _ = db.FamilyMembers.Add(member);
        try
        {
            _ = await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            return Conflict(new { error = "A family member with this display name already exists." });
        }
        return Ok(new { member.Id, member.FamilyId, member.DisplayName, member.Role, member.CreatedAt });
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = AuthorizationPolicies.FamilyAdmin)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var member = await db.FamilyMembers.FindAsync([id], cancellationToken);
        if (member is null) return NotFound();
        await families.RequireAccessAsync(member.FamilyId, cancellationToken);
        var family = await db.FamilySpaces.AsNoTracking().FirstOrDefaultAsync(
            candidate => candidate.Id == member.FamilyId,
            cancellationToken);
        if (family is not null && OwnerRecoveryPolicy.IsPrimaryOwner(family, member))
        {
            if (!User.IsInRole(FamilyRoles.Owner)) return Forbid();
            return Conflict(new { error = "The primary family owner cannot be deleted." });
        }
        if (member.Role == FamilyRoles.Owner)
        {
            if (!User.IsInRole(FamilyRoles.Owner)) return Forbid();
            var ownerCount = await db.FamilyMembers.AsNoTracking().CountAsync(
                candidate => candidate.FamilyId == member.FamilyId && candidate.Role == FamilyRoles.Owner,
                cancellationToken);
            if (ownerCount <= 1) return Conflict(new { error = "The last family owner cannot be deleted." });
        }

        var sessions = await db.MemberSessions.Where(session => session.MemberId == member.Id).ToListAsync(cancellationToken);
        db.MemberSessions.RemoveRange(sessions);
        _ = db.FamilyMembers.Remove(member);
        _ = await db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    private static readonly HashSet<string> AllowedRoles = new(StringComparer.Ordinal)
    {
        FamilyRoles.Owner,
        FamilyRoles.Admin,
        FamilyRoles.Member
    };

    public sealed record CreateMemberRequest(Guid? FamilyId, string? DisplayName, string? Role, string? Password);
}
