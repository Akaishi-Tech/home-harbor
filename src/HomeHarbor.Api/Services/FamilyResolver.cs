using System.Security.Claims;
using HomeHarbor.Api.Auth;
using HomeHarbor.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace HomeHarbor.Api.Services;

public sealed class FamilyResolver(HomeHarborDbContext db, IHttpContextAccessor httpContextAccessor) : IFamilyResolver
{
    public async Task<Guid?> ResolveAsync(Guid? requestedFamilyId, CancellationToken cancellationToken)
    {
        var userFamilyId = CurrentUserFamilyId();
        return userFamilyId is { } scopedFamilyId
            ? (Guid?)(requestedFamilyId is { } requested && requested != scopedFamilyId
                ? throw new UnauthorizedAccessException("Family access is denied for the current session.")
                : await db.FamilySpaces.AsNoTracking().AnyAsync(f => f.Id == scopedFamilyId, cancellationToken)
                ? scopedFamilyId
                : null)
            : requestedFamilyId is { } id
            ? await db.FamilySpaces.AsNoTracking().AnyAsync(f => f.Id == id, cancellationToken) ? id : null
            : await db.FamilySpaces
            .AsNoTracking()
            .OrderBy(f => f.CreatedAt)
            .Select(f => (Guid?)f.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task RequireAccessAsync(Guid familyId, CancellationToken cancellationToken)
    {
        var resolved = await ResolveAsync(familyId, cancellationToken);
        if (resolved != familyId)
            throw new UnauthorizedAccessException("Family access is denied for the current session.");
    }

    private Guid? CurrentUserFamilyId()
    {
        var user = httpContextAccessor.HttpContext?.User;
        if (user?.Identity?.IsAuthenticated != true) return null;
        if (!string.Equals(user.FindFirstValue(AuthClaims.TokenKind), AuthTokenKinds.User, StringComparison.Ordinal))
            return null;
        return Guid.TryParse(user.FindFirstValue(AuthClaims.FamilyId), out var familyId)
            ? familyId
            : throw new UnauthorizedAccessException("The current session is missing its family identity.");
    }
}
