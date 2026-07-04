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
    ICertificateService certificates,
    IReverseProxyConfigService proxyConfig) : ControllerBase
{
    [HttpGet("certificates")]
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
    public async Task<IActionResult> CreateSelfSigned([FromBody] CreateCertificateRequest request, CancellationToken cancellationToken)
    {
        var resolved = await families.ResolveAsync(request.FamilyId, cancellationToken);
        if (resolved is null) return BadRequest(new { error = "Create a family space first." });
        if (string.IsNullOrWhiteSpace(request.Hostname)) return BadRequest(new { error = "hostname is required." });

        var hostname = request.Hostname.Trim().ToLowerInvariant();
        var generated = certificates.CreateSelfSigned(hostname, TimeSpan.FromDays(Math.Clamp(request.Days ?? 365, 1, 3650)));
        var record = await db.Certificates.FirstOrDefaultAsync(
            c => c.FamilyId == resolved.Value && c.Hostname == hostname,
            cancellationToken);
        if (record is null)
        {
            record = new CertificateEntity
            {
                Id = Guid.NewGuid(),
                FamilyId = resolved.Value,
                Hostname = hostname,
                CreatedAt = DateTimeOffset.UtcNow
            };
            _ = db.Certificates.Add(record);
        }

        record.Kind = "self-signed";
        record.CertificatePem = generated.CertificatePem;
        record.PrivateKeyPem = generated.PrivateKeyPem;
        record.NotBefore = generated.NotBefore;
        record.NotAfter = generated.NotAfter;
        _ = await db.SaveChangesAsync(cancellationToken);
        return Ok(new { record.Id, record.Hostname, record.Kind, record.NotBefore, record.NotAfter, record.CertificatePem });
    }

    [HttpGet("proxy/routes")]
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
    public async Task<IActionResult> AddRoute([FromBody] AddProxyRouteRequest request, CancellationToken cancellationToken)
    {
        var resolved = await families.ResolveAsync(request.FamilyId, cancellationToken);
        if (resolved is null) return BadRequest(new { error = "Create a family space first." });
        if (!ReverseProxyConfigService.TryNormalizeHostname(request.Hostname, out var hostname, out var hostnameError))
            return BadRequest(new { error = hostnameError });
        if (!ReverseProxyConfigService.TryNormalizeUpstreamUrl(request.UpstreamUrl, out var upstreamUrl, out var upstreamUrlError))
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
