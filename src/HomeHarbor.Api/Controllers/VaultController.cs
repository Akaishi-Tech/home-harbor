using HomeHarbor.Api.Auth;
using HomeHarbor.Api.Data;
using HomeHarbor.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HomeHarbor.Api.Controllers;

[ApiController]
[Authorize(Policy = AuthorizationPolicies.FamilyAdmin)]
[Route("api/vault/items")]
public sealed class VaultController(HomeHarborDbContext db, IFamilyResolver families) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] Guid? familyId, CancellationToken cancellationToken)
    {
        var resolved = await families.ResolveAsync(familyId, cancellationToken);
        if (resolved is null) return BadRequest(new { error = "Create a family space first." });

        var items = await db.VaultItems
            .AsNoTracking()
            .Where(i => i.FamilyId == resolved.Value)
            .OrderBy(i => i.Name)
            .Select(i => new { i.Id, i.FamilyId, i.Name, i.KeyHint, i.CreatedAt, i.UpdatedAt })
            .ToListAsync(cancellationToken);
        return Ok(items);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        var item = await db.VaultItems.AsNoTracking().FirstOrDefaultAsync(i => i.Id == id, cancellationToken);
        if (item is null) return NotFound();
        await families.RequireAccessAsync(item.FamilyId, cancellationToken);
        return Ok(new { item.Id, item.FamilyId, item.Name, item.EncryptedPayload, item.Nonce, item.KeyHint, item.CreatedAt, item.UpdatedAt });
    }

    [HttpPost]
    public async Task<IActionResult> Upsert([FromBody] UpsertVaultItemRequest request, CancellationToken cancellationToken)
    {
        var resolved = await families.ResolveAsync(request.FamilyId, cancellationToken);
        if (resolved is null) return BadRequest(new { error = "Create a family space first." });
        if (string.IsNullOrWhiteSpace(request.Name) ||
            string.IsNullOrWhiteSpace(request.EncryptedPayload) ||
            string.IsNullOrWhiteSpace(request.Nonce) ||
            string.IsNullOrWhiteSpace(request.KeyHint))
        {
            return BadRequest(new { error = "Name, encryptedPayload, nonce, and keyHint are required." });
        }
        if (request.Name.Trim().Length > 160 ||
            request.EncryptedPayload.Length > 768 * 1024 ||
            request.Nonce.Length > 128 ||
            request.KeyHint.Length > 128)
        {
            return BadRequest(new { error = "Vault item fields exceed their allowed size." });
        }

        var now = DateTimeOffset.UtcNow;
        var item = request.Id is { } id
            ? await db.VaultItems.FirstOrDefaultAsync(i => i.Id == id && i.FamilyId == resolved.Value, cancellationToken)
            : null;
        if (item is null)
        {
            item = new VaultItemEntity
            {
                Id = request.Id ?? Guid.NewGuid(),
                FamilyId = resolved.Value,
                CreatedAt = now
            };
            _ = db.VaultItems.Add(item);
        }

        item.Name = request.Name.Trim();
        item.EncryptedPayload = request.EncryptedPayload;
        item.Nonce = request.Nonce;
        item.KeyHint = request.KeyHint;
        item.UpdatedAt = now;
        _ = await db.SaveChangesAsync(cancellationToken);
        return Ok(new { item.Id, item.FamilyId, item.Name, item.KeyHint, item.CreatedAt, item.UpdatedAt });
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var item = await db.VaultItems.FindAsync([id], cancellationToken);
        if (item is null) return NotFound();
        await families.RequireAccessAsync(item.FamilyId, cancellationToken);
        _ = db.VaultItems.Remove(item);
        _ = await db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    public sealed record UpsertVaultItemRequest(
        Guid? Id,
        Guid? FamilyId,
        string Name,
        string EncryptedPayload,
        string Nonce,
        string KeyHint);
}
