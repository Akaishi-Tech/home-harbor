using HomeHarbor.Api.Data;
using HomeHarbor.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HomeHarbor.Api.Controllers;

[ApiController]
[Route("api/remote/wireguard")]
public sealed class RemoteAccessController(
    HomeHarborDbContext db,
    IFamilyResolver families,
    IWireGuardKeyGenerator keyGenerator) : ControllerBase
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
    public async Task<IActionResult> Create([FromBody] CreatePeerRequest request, CancellationToken cancellationToken)
    {
        var familyId = await families.ResolveAsync(request.FamilyId, cancellationToken);
        if (familyId is null) return BadRequest(new { error = "Create a family space first." });

        var peerNumber = await db.WireGuardPeers.AsNoTracking().CountAsync(p => p.FamilyId == familyId.Value, cancellationToken) + 2;
        var keys = await keyGenerator.GenerateAsync(cancellationToken);
        var address = $"10.44.0.{peerNumber}/32";
        var peer = new WireGuardPeerEntity
        {
            Id = Guid.NewGuid(),
            FamilyId = familyId.Value,
            Name = string.IsNullOrWhiteSpace(request.Name) ? "mobile device" : request.Name.Trim(),
            PrivateKey = keys.PrivateKey,
            PublicKey = keys.PublicKey,
            Address = address,
            CreatedAt = DateTimeOffset.UtcNow
        };

        _ = db.WireGuardPeers.Add(peer);
        _ = await db.SaveChangesAsync(cancellationToken);

        var endpoint = string.IsNullOrWhiteSpace(request.Endpoint) ? "homeharbor.local:51820" : request.Endpoint.Trim();
        var config = $"""
            [Interface]
            PrivateKey = {keys.PrivateKey}
            Address = {address}
            DNS = 10.44.0.1

            [Peer]
            PublicKey = SERVER_PUBLIC_KEY_PLACEHOLDER
            AllowedIPs = 10.44.0.0/24
            Endpoint = {endpoint}
            PersistentKeepalive = 25
            """;

        return Ok(new { peer.Id, peer.Name, peer.PublicKey, peer.Address, peer.CreatedAt, config, keyGenerationMode = keys.Mode });
    }

    public sealed record CreatePeerRequest(Guid? FamilyId, string? Name, string? Endpoint);
}
