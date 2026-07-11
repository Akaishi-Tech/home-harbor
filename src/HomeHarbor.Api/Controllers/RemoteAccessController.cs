using HomeHarbor.Api.Auth;
using HomeHarbor.Api.Data;
using HomeHarbor.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HomeHarbor.Api.Controllers;

[ApiController]
[Authorize(Policy = AuthorizationPolicies.FamilyAdmin)]
[Route("api/remote/wireguard")]
public sealed class RemoteAccessController(
    HomeHarborDbContext db,
    IFamilyResolver families) : ControllerBase
{
    [HttpGet("peers")]
    public async Task<IActionResult> List([FromQuery] Guid? familyId, CancellationToken cancellationToken)
    {
        var resolved = await families.ResolveAsync(familyId, cancellationToken);
        if (resolved is null) return BadRequest(new { error = "Create a family space first." });

        var peers = await db.WireGuardPeers
            .AsNoTracking()
            .Where(p => p.FamilyId == resolved.Value)
            .OrderByDescending(p => p.CreatedAt)
            .Select(p => new { p.Id, p.FamilyId, p.Name, p.PublicKey, p.Address, p.CreatedAt, p.LastHandshakeAt })
            .ToListAsync(cancellationToken);
        return Ok(peers);
    }

    [HttpPost("peers")]
    [Authorize(Policy = AuthorizationPolicies.FamilyAdmin)]
    public IActionResult Create([FromBody] CreatePeerRequest request)
    {
        _ = request;
        return StatusCode(StatusCodes.Status501NotImplemented, new
        {
            error = "WireGuard peer provisioning is unavailable until the appliance server-key and apply pipeline is installed."
        });
    }

    public sealed record CreatePeerRequest(Guid? FamilyId, string? Name, string? Endpoint);
}
