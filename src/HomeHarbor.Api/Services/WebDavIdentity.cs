using System.Security.Claims;
using HomeHarbor.Api.Auth;
using HomeHarbor.Core.Identity;
using HomeHarbor.Core.Storage;

namespace HomeHarbor.Api.Services;

public sealed record WebDavIdentity(Guid FamilyId, Guid? DeviceId, WebDavTokenScope Scope)
{
    public static WebDavIdentity FromPrincipal(ClaimsPrincipal principal)
    {
        var familyIdRaw = principal.FindFirstValue(BasicAuthenticationHandler.FamilyIdClaim)
            ?? throw new InvalidOperationException("Missing WebDAV family claim.");
        var scopeRaw = principal.FindFirstValue(BasicAuthenticationHandler.WebDavScopeClaim)
            ?? throw new InvalidOperationException("Missing WebDAV scope claim.");
        var deviceRaw = principal.FindFirstValue(BasicAuthenticationHandler.DeviceIdClaim);

        return new WebDavIdentity(
            Guid.Parse(familyIdRaw),
            string.IsNullOrWhiteSpace(deviceRaw) ? null : Guid.Parse(deviceRaw),
            Enum.Parse<WebDavTokenScope>(scopeRaw));
    }

    public bool CanAccess(StorageArea area) => Scope switch
    {
        WebDavTokenScope.All => true,
        WebDavTokenScope.Files => area == StorageArea.Files,
        WebDavTokenScope.Photos => area == StorageArea.Photos,
        WebDavTokenScope.Backups => area == StorageArea.Backups,
        _ => false
    };
}

