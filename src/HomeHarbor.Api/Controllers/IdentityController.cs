using System.Security.Claims;
using HomeHarbor.Api.Auth;
using HomeHarbor.Api.Data;
using HomeHarbor.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HomeHarbor.Api.Controllers;

[ApiController]
[Route("api/identity")]
public sealed class IdentityController(HomeHarborDbContext db, IFamilyResolver families, IJwtTokenService jwtTokens) : ControllerBase
{
    [AllowAnonymous]
    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest request, CancellationToken cancellationToken)
    {
        var familyId = await families.ResolveAsync(request.FamilyId, cancellationToken);
        if (familyId is null) return BadRequest(new { error = "Create a family space first." });
        var family = await db.FamilySpaces.AsNoTracking().FirstOrDefaultAsync(f => f.Id == familyId.Value, cancellationToken);
        if (family is null) return BadRequest(new { error = "Create a family space first." });
        var member = await db.FamilyMembers.AsNoTracking().FirstOrDefaultAsync(
            m => m.FamilyId == familyId.Value && m.DisplayName == request.DisplayName,
            cancellationToken);
        if (member is null || string.IsNullOrWhiteSpace(member.PasswordHash))
            return Unauthorized(new { error = "Invalid credentials." });
        if (!BCrypt.Net.BCrypt.Verify(request.Password, member.PasswordHash))
            return Unauthorized(new { error = "Invalid credentials." });

        var tokenId = jwtTokens.GenerateTokenId();
        var session = new MemberSessionEntity
        {
            Id = Guid.NewGuid(),
            FamilyId = familyId.Value,
            MemberId = member.Id,
            TokenHash = JwtTokenService.HashTokenId(tokenId),
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.Add(jwtTokens.UserAccessTokenLifetime)
        };

        _ = db.MemberSessions.Add(session);
        _ = await db.SaveChangesAsync(cancellationToken);
        var accessToken = jwtTokens.IssueUserAccessToken(session, member, family, tokenId);
        return Ok(new
        {
            accessToken,
            tokenType = "Bearer",
            session.ExpiresAt,
            member = new { member.Id, member.DisplayName, member.Role },
            family = new { family.Id, family.Name, family.OwnerDisplayName, family.CreatedAt }
        });
    }

    [HttpGet("session")]
    public async Task<IActionResult> Session(CancellationToken cancellationToken)
    {
        if (!TryCurrentSessionId(out var sessionId)) return Unauthorized();
        var session = await db.MemberSessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken);
        if (session is null || session.ExpiresAt <= DateTimeOffset.UtcNow) return Unauthorized();
        var member = await db.FamilyMembers.AsNoTracking().FirstOrDefaultAsync(m => m.Id == session.MemberId, cancellationToken);
        var family = await db.FamilySpaces.AsNoTracking().FirstOrDefaultAsync(f => f.Id == session.FamilyId, cancellationToken);
        return Ok(new
        {
            session.Id,
            session.FamilyId,
            session.ExpiresAt,
            member = member is null ? null : new { member.Id, member.DisplayName, member.Role },
            family = family is null ? null : new { family.Id, family.Name, family.OwnerDisplayName, family.CreatedAt }
        });
    }

    [HttpPost("logout")]
    public async Task<IActionResult> Logout(CancellationToken cancellationToken)
    {
        if (!TryCurrentSessionId(out var sessionId)) return NoContent();
        var session = await db.MemberSessions.FindAsync([sessionId], cancellationToken);
        if (session is not null)
        {
            _ = db.MemberSessions.Remove(session);
            _ = await db.SaveChangesAsync(cancellationToken);
        }

        return NoContent();
    }

    private bool TryCurrentSessionId(out Guid sessionId)
        => Guid.TryParse(User.FindFirstValue(AuthClaims.SessionId), out sessionId);

    public sealed record LoginRequest(Guid? FamilyId, string DisplayName, string Password);
}
