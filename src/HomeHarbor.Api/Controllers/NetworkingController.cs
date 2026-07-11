using HomeHarbor.Api.Auth;
using HomeHarbor.Api.Data;
using HomeHarbor.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HomeHarbor.Api.Controllers;

[ApiController]
[Route("api/networking")]
public sealed class NetworkingController(
    HomeHarborDbContext db,
    IFamilyResolver families,
    IReverseProxyConfigService proxyConfig,
    IRuntimeSignalService signals) : ControllerBase
{
    [HttpGet("certificates")]
    [Authorize(Policy = AuthorizationPolicies.FamilyAdmin)]
    public async Task<IActionResult> Certificates([FromQuery] Guid? familyId, CancellationToken cancellationToken)
    {
        var resolved = await families.ResolveAsync(familyId, cancellationToken);
        return resolved is null
            ? BadRequest(new { error = "Create a family space first." })
            : Ok(await db.Certificates
            .AsNoTracking()
            .Where(c => c.FamilyId == resolved.Value)
            .OrderBy(c => c.Hostname)
            .Select(c => new { c.Id, c.FamilyId, c.Hostname, c.Kind, c.NotBefore, c.NotAfter, c.CreatedAt })
            .ToListAsync(cancellationToken));
    }

    [HttpPost("certificates/self-signed")]
    [Authorize(Policy = AuthorizationPolicies.FamilyAdmin)]
    public IActionResult CreateSelfSigned([FromBody] CreateCertificateRequest request)
    {
        _ = request;
        return StatusCode(StatusCodes.Status501NotImplemented, new
        {
            error = "Certificate provisioning is unavailable until private keys can be stored outside the database and activated by the reverse proxy."
        });
    }

    [HttpGet("proxy/routes")]
    [Authorize(Policy = AuthorizationPolicies.FamilyAdmin)]
    public async Task<IActionResult> Routes([FromQuery] Guid? familyId, CancellationToken cancellationToken)
    {
        var resolved = await families.ResolveAsync(familyId, cancellationToken);
        return resolved is null
            ? BadRequest(new { error = "Create a family space first." })
            : Ok(await db.ReverseProxyRoutes
            .AsNoTracking()
            .Where(r => r.FamilyId == resolved.Value)
            .OrderBy(r => r.Hostname)
            .ToListAsync(cancellationToken));
    }

    [HttpPost("proxy/routes")]
    [Authorize(Policy = AuthorizationPolicies.FamilyAdmin)]
    public async Task<IActionResult> AddRoute([FromBody] AddProxyRouteRequest request, CancellationToken cancellationToken)
    {
        var resolved = await families.ResolveAsync(request.FamilyId, cancellationToken);
        if (resolved is null) return BadRequest(new { error = "Create a family space first." });
        if (!ReverseProxyConfigService.TryNormalizeHostname(request.Hostname, out var hostname, out var hostnameError))
            return BadRequest(new { error = hostnameError });
        if (string.Equals(hostname, ReverseProxyConfigService.ControlPlaneHostname, StringComparison.OrdinalIgnoreCase))
            return Conflict(new { error = "homeharbor.local is reserved for the control plane." });
        if (hostname.EndsWith(".local", StringComparison.OrdinalIgnoreCase) && request.TlsEnabled)
            return BadRequest(new { error = ".local routes must use the appliance internal CA rather than public ACME." });
        if (!ReverseProxyConfigService.TryNormalizeUserUpstreamUrl(request.UpstreamUrl, out var upstreamUrl, out var upstreamUrlError))
            return BadRequest(new { error = upstreamUrlError });

        var route = await db.ReverseProxyRoutes.FirstOrDefaultAsync(
            r => r.FamilyId == resolved.Value && r.Hostname == hostname,
            cancellationToken);
        if (route is null)
        {
            route = new ReverseProxyRouteEntity
            {
                Id = Guid.NewGuid(),
                FamilyId = resolved.Value,
                Hostname = hostname,
                CreatedAt = DateTimeOffset.UtcNow
            };
            _ = db.ReverseProxyRoutes.Add(route);
        }

        route.UpstreamUrl = upstreamUrl;
        route.TlsEnabled = request.TlsEnabled;
        _ = await db.SaveChangesAsync(cancellationToken);
        signals.RequestCaddyRender();
        return Ok(route);
    }

    [Authorize(Policy = AuthorizationPolicies.Automation)]
    [HttpGet("proxy/caddyfile")]
    public async Task<IActionResult> Caddyfile([FromQuery] Guid? familyId, CancellationToken cancellationToken)
    {
        var resolved = await families.ResolveAsync(familyId, cancellationToken);
        var routes = resolved is null
            ? []
            : await db.ReverseProxyRoutes.AsNoTracking().Where(r => r.FamilyId == resolved.Value).ToListAsync(cancellationToken);
        return Content(proxyConfig.BuildCaddyfile(routes), "text/plain");
    }

    public sealed record CreateCertificateRequest(Guid? FamilyId, string Hostname, int? Days);
    public sealed record AddProxyRouteRequest(Guid? FamilyId, string Hostname, string UpstreamUrl, bool TlsEnabled);
}
