using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
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
public sealed class IdentityController(
    HomeHarborDbContext db,
    IFamilyResolver families,
    IJwtTokenService jwtTokens,
    ITokenGenerator tokenGenerator,
    ISetupPairingService pairings,
    AuthenticationFailureThrottle throttle) : ControllerBase
{
    private static readonly SemaphoreSlim RecoveryGate = new(1, 1);

    [AllowAnonymous]
    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.DisplayName) || request.DisplayName.Length > 96 ||
            string.IsNullOrEmpty(request.Password) || request.Password.Length > 128)
        {
            return Unauthorized(new { error = "Invalid credentials." });
        }

        var familyId = await families.ResolveAsync(request.FamilyId, cancellationToken);
        if (familyId is null) return BadRequest(new { error = "Create a family space first." });
        var clientIdentity = AuthenticationClientIdentity.Resolve(HttpContext);
        var throttleIdentity = $"{familyId:N}:{request.DisplayName}";
        var clientAllowed = throttle.TryAcquire("login-client", clientIdentity, out var clientRetryAfter);
        var accountAllowed = throttle.TryAcquire("login", throttleIdentity, out var accountRetryAfter);
        if (!clientAllowed || !accountAllowed)
        {
            var retryAfter = clientRetryAfter > accountRetryAfter ? clientRetryAfter : accountRetryAfter;
            Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
            return StatusCode(StatusCodes.Status429TooManyRequests, new { error = "Too many failed sign-in attempts. Try again later." });
        }

        var family = await db.FamilySpaces.AsNoTracking().FirstOrDefaultAsync(f => f.Id == familyId.Value, cancellationToken);
        if (family is null) return BadRequest(new { error = "Create a family space first." });
        var member = await db.FamilyMembers.AsNoTracking().FirstOrDefaultAsync(
            m => m.FamilyId == familyId.Value && m.DisplayName == request.DisplayName,
            cancellationToken);
        if (!LocalPasswordVerifier.Verify(request.Password, member?.PasswordHash) || member is null)
        {
            throttle.RecordFailure("login-client", clientIdentity);
            throttle.RecordFailure("login", throttleIdentity);
            return Unauthorized(new { error = "Invalid credentials." });
        }
        throttle.RecordSuccess("login", throttleIdentity);
        if (member.Role is not (HomeHarbor.Core.Identity.FamilyRoles.Owner or
            HomeHarbor.Core.Identity.FamilyRoles.Admin or
            HomeHarbor.Core.Identity.FamilyRoles.Member))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                error = "This legacy family role is unavailable until per-member storage isolation is implemented."
            });
        }

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

    [AllowAnonymous]
    [HttpPost("recover-owner")]
    public async Task<IActionResult> RecoverOwner(
        [FromBody] RecoverOwnerRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.RecoveryCode) ||
            string.IsNullOrWhiteSpace(request.NewPassword) ||
            request.NewPassword.Length is < 12 or > 128)
        {
            return BadRequest(new { error = "Recovery code is invalid or the new password does not meet policy." });
        }

        var clientIdentity = AuthenticationClientIdentity.Resolve(HttpContext);
        var throttleIdentity = request.FamilyId?.ToString("N") ?? "default-family";
        var clientAllowed = throttle.TryAcquire("owner-recovery-client", clientIdentity, out var clientRetryAfter);
        var accountAllowed = throttle.TryAcquire("owner-recovery", throttleIdentity, out var accountRetryAfter);
        if (!clientAllowed || !accountAllowed)
        {
            var retryAfter = clientRetryAfter > accountRetryAfter ? clientRetryAfter : accountRetryAfter;
            Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
            return StatusCode(StatusCodes.Status429TooManyRequests, new { error = "Too many failed recovery attempts. Try again later." });
        }

        await RecoveryGate.WaitAsync(cancellationToken);
        try
        {
            var family = request.FamilyId is { } familyId
                ? await db.FamilySpaces.FirstOrDefaultAsync(item => item.Id == familyId, cancellationToken)
                : await db.FamilySpaces.OrderBy(item => item.CreatedAt).FirstOrDefaultAsync(cancellationToken);
            var ownerCandidates = family is null
                ? []
                : await db.FamilyMembers
                .Where(member => member.FamilyId == family.Id &&
                    member.Role == HomeHarbor.Core.Identity.FamilyRoles.Owner)
                .ToListAsync(cancellationToken);
            var owner = family is null ? null : OwnerRecoveryPolicy.FindPrimaryOwner(family, ownerCandidates);
            var physicalLegacyEnrollment = family is not null && owner is not null &&
                OwnerRecoveryPolicy.RequiresPhysicalLegacyEnrollment(family, owner) &&
                pairings.IsBootstrapCodeValid(request.RecoveryCode);
            var recoveryCodeValid = family is not null &&
                OwnerRecoveryPolicy.IsRecoveryCodeValid(family, request.RecoveryCode);
            if (family is null || owner is null || (!recoveryCodeValid && !physicalLegacyEnrollment))
            {
                throttle.RecordFailure("owner-recovery-client", clientIdentity);
                throttle.RecordFailure("owner-recovery", throttleIdentity);
                return Unauthorized(new { error = "Invalid recovery credentials." });
            }

            var replacementCode = tokenGenerator.GenerateRecoveryCode();
            owner.PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.NewPassword);
            family.RecoveryCodeHash = BCrypt.Net.BCrypt.HashPassword(replacementCode);
            var sessions = await db.MemberSessions
                .Where(session => session.FamilyId == family.Id && session.MemberId == owner.Id)
                .ToListAsync(cancellationToken);
            db.MemberSessions.RemoveRange(sessions);
            _ = await db.SaveChangesAsync(cancellationToken);
            if (physicalLegacyEnrollment) pairings.ConsumeBootstrapCode(request.RecoveryCode);
            throttle.RecordSuccess("owner-recovery", throttleIdentity);
            return Ok(new
            {
                recovered = true,
                familyId = family.Id,
                ownerId = owner.Id,
                owner.DisplayName,
                replacementRecoveryCode = replacementCode
            });
        }
        finally
        {
            _ = RecoveryGate.Release();
        }
    }

    [HttpPost("recovery-code/rotate")]
    [Authorize(Policy = AuthorizationPolicies.FamilyOwner)]
    public async Task<IActionResult> RotateRecoveryCode(
        [FromBody] RotateRecoveryCodeRequest request,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(User.FindFirstValue(AuthClaims.FamilyId), out var familyId) ||
            !TryCurrentMemberId(out var memberId))
        {
            return Unauthorized();
        }

        var clientIdentity = AuthenticationClientIdentity.Resolve(HttpContext);
        var throttleIdentity = $"{familyId:N}:{memberId:N}";
        var clientAllowed = throttle.TryAcquire("recovery-rotate-client", clientIdentity, out var clientRetryAfter);
        var accountAllowed = throttle.TryAcquire("recovery-rotate", throttleIdentity, out var accountRetryAfter);
        if (!clientAllowed || !accountAllowed)
        {
            var retryAfter = clientRetryAfter > accountRetryAfter ? clientRetryAfter : accountRetryAfter;
            Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
            return StatusCode(StatusCodes.Status429TooManyRequests, new { error = "Too many failed recovery-code rotation attempts. Try again later." });
        }

        if (string.IsNullOrEmpty(request.CurrentPassword) || request.CurrentPassword.Length > 128)
        {
            throttle.RecordFailure("recovery-rotate-client", clientIdentity);
            throttle.RecordFailure("recovery-rotate", throttleIdentity);
            return Unauthorized(new { error = "Current password is invalid." });
        }

        await RecoveryGate.WaitAsync(cancellationToken);
        try
        {
            var family = await db.FamilySpaces.FirstOrDefaultAsync(
                item => item.Id == familyId,
                cancellationToken);
            var currentMember = await db.FamilyMembers.AsNoTracking().FirstOrDefaultAsync(
                member => member.Id == memberId && member.FamilyId == familyId,
                cancellationToken);
            if (family is null ||
                currentMember is null ||
                !OwnerRecoveryPolicy.TryRotateRecoveryCode(
                    family,
                    currentMember,
                    request.CurrentPassword,
                    tokenGenerator,
                    out var recoveryCode))
            {
                throttle.RecordFailure("recovery-rotate-client", clientIdentity);
                throttle.RecordFailure("recovery-rotate", throttleIdentity);
                return Unauthorized(new { error = "Current password is invalid." });
            }

            _ = await db.SaveChangesAsync(cancellationToken);
            throttle.RecordSuccess("recovery-rotate", throttleIdentity);
            return Ok(new
            {
                rotated = true,
                familyId = family.Id,
                recoveryCode
            });
        }
        finally
        {
            _ = RecoveryGate.Release();
        }
    }

    private bool TryCurrentSessionId(out Guid sessionId)
        => Guid.TryParse(User.FindFirstValue(AuthClaims.SessionId), out sessionId);

    private bool TryCurrentMemberId(out Guid memberId)
        => Guid.TryParse(
            User.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? User.FindFirstValue(ClaimTypes.NameIdentifier),
            out memberId);

    public sealed record LoginRequest(Guid? FamilyId, string? DisplayName, string? Password);
    public sealed record RecoverOwnerRequest(Guid? FamilyId, string? RecoveryCode, string? NewPassword);
    public sealed record RotateRecoveryCodeRequest(string? CurrentPassword);
}
