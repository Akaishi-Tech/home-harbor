using System.Text.RegularExpressions;
using HomeHarbor.Api.Auth;
using HomeHarbor.Api.Data;
using HomeHarbor.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HomeHarbor.Api.Controllers;

[ApiController]
[Route("api/smb")]
public sealed partial class SmbController(
    HomeHarborDbContext db,
    IFamilyResolver families,
    IHomeHarborStorageService storage,
    ISmbConfigService config,
    IRuntimeSignalService signals,
    ITokenGenerator tokens) : ControllerBase
{
    private const int SmbUserPoolSize = 32;

    [HttpGet("shares")]
    [Authorize(Policy = AuthorizationPolicies.FamilyAdmin)]
    public async Task<IActionResult> Shares([FromQuery] Guid? familyId, CancellationToken cancellationToken)
    {
        var resolved = await families.ResolveAsync(familyId, cancellationToken);
        if (resolved is null) return BadRequest(new { error = "Create a family space first." });

        var shares = await db.SmbShares
            .AsNoTracking()
            .Where(s => s.FamilyId == resolved.Value)
            .OrderBy(s => s.ShareName)
            .ToListAsync(cancellationToken);
        var credentialCounts = await db.SmbCredentials
            .AsNoTracking()
            .Where(c => c.FamilyId == resolved.Value && c.RevokedAt == null)
            .GroupBy(c => c.ShareId)
            .Select(g => new { ShareId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.ShareId, g => g.Count, cancellationToken);

        return Ok(shares.Select(s => ShareResponse(s, credentialCounts.GetValueOrDefault(s.Id))));
    }

    [HttpPost("shares")]
    [Authorize(Policy = AuthorizationPolicies.FamilyAdmin)]
    public async Task<IActionResult> CreateShare([FromBody] CreateSmbShareRequest request, CancellationToken cancellationToken)
    {
        var resolved = await families.ResolveAsync(request.FamilyId, cancellationToken);
        if (resolved is null) return BadRequest(new { error = "Create a family space first." });

        try
        {
            var share = await EnsureShareAsync(
                resolved.Value,
                request.Name,
                request.ShareName,
                request.ReadOnly ?? false,
                request.Enabled ?? true,
                cancellationToken);
            _ = await db.SaveChangesAsync(cancellationToken);
            signals.RequestSmbApply();
            return Ok(ShareResponse(share, await ActiveCredentialCountAsync(share.Id, cancellationToken)));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPut("shares/{id:guid}")]
    [Authorize(Policy = AuthorizationPolicies.FamilyAdmin)]
    public async Task<IActionResult> UpdateShare(Guid id, [FromBody] UpdateSmbShareRequest request, CancellationToken cancellationToken)
    {
        var share = await db.SmbShares.FindAsync([id], cancellationToken);
        if (share is null) return NotFound(new { error = "SMB share not found." });
        await families.RequireAccessAsync(share.FamilyId, cancellationToken);

        if (!string.IsNullOrWhiteSpace(request.Name)) share.Name = request.Name.Trim();
        if (request.ReadOnly is { } readOnly) share.ReadOnly = readOnly;
        if (request.Enabled is { } enabled) share.Enabled = enabled;
        share.RuntimeState = "pending";
        share.LastError = string.Empty;
        share.UpdatedAt = DateTimeOffset.UtcNow;
        _ = await db.SaveChangesAsync(cancellationToken);
        signals.RequestSmbApply();
        return Ok(ShareResponse(share, await ActiveCredentialCountAsync(share.Id, cancellationToken)));
    }

    [HttpGet("credentials")]
    [Authorize(Policy = AuthorizationPolicies.FamilyAdmin)]
    public async Task<IActionResult> Credentials([FromQuery] Guid? familyId, [FromQuery] Guid? shareId, CancellationToken cancellationToken)
    {
        var resolved = await families.ResolveAsync(familyId, cancellationToken);
        if (resolved is null) return BadRequest(new { error = "Create a family space first." });

        var query = db.SmbCredentials.AsNoTracking().Where(c => c.FamilyId == resolved.Value && c.RevokedAt == null);
        if (shareId is { } id) query = query.Where(c => c.ShareId == id);

        return Ok(await query
            .OrderBy(c => c.DisplayName)
            .Select(c => CredentialResponse(c, includePassword: null))
            .ToListAsync(cancellationToken));
    }

    [HttpPost("credentials")]
    [Authorize(Policy = AuthorizationPolicies.FamilyAdmin)]
    public async Task<IActionResult> CreateCredential([FromBody] CreateSmbCredentialRequest request, CancellationToken cancellationToken)
    {
        var resolved = await families.ResolveAsync(request.FamilyId, cancellationToken);
        if (resolved is null) return BadRequest(new { error = "Create a family space first." });
        if (request.DisplayName?.Trim().Length > 96)
            return BadRequest(new { error = "displayName must not exceed 96 characters." });

        var share = request.ShareId is { } shareId
            ? await db.SmbShares.FirstOrDefaultAsync(s => s.Id == shareId && s.FamilyId == resolved.Value, cancellationToken)
            : await EnsureShareAsync(resolved.Value, null, null, false, true, cancellationToken);
        if (share is null) return NotFound(new { error = "SMB share not found." });

        var unixUser = await AllocateUnixUserAsync(cancellationToken);
        if (unixUser is null) return Conflict(new { error = "The SMB credential pool is exhausted." });

        var password = tokens.GenerateSecret(24);
        var now = DateTimeOffset.UtcNow;
        var credential = new SmbCredentialEntity
        {
            Id = Guid.NewGuid(),
            FamilyId = resolved.Value,
            ShareId = share.Id,
            DisplayName = string.IsNullOrWhiteSpace(request.DisplayName) ? "SMB credential" : request.DisplayName.Trim(),
            Username = unixUser,
            UnixUser = unixUser,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(password),
            ReadOnly = request.ReadOnly ?? false,
            Enabled = true,
            RuntimeState = "pending",
            CreatedAt = now,
            RotatedAt = now
        };
        _ = db.SmbCredentials.Add(credential);
        share.RuntimeState = "pending";
        share.UpdatedAt = now;
        try
        {
            _ = await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            return Conflict(new { error = "The selected SMB credential slot was claimed concurrently; retry the request." });
        }
        await signals.WriteSmbPasswordAsync(credential.Id, credential.Username, credential.UnixUser, password, cancellationToken);
        signals.RequestSmbApply();

        return Ok(CredentialResponse(credential, password));
    }

    [HttpDelete("credentials/{id:guid}")]
    [Authorize(Policy = AuthorizationPolicies.FamilyAdmin)]
    public async Task<IActionResult> RevokeCredential(Guid id, CancellationToken cancellationToken)
    {
        var credential = await db.SmbCredentials.FindAsync([id], cancellationToken);
        if (credential is null) return NotFound(new { error = "SMB credential not found." });
        await families.RequireAccessAsync(credential.FamilyId, cancellationToken);

        credential.Enabled = false;
        credential.RevokedAt = DateTimeOffset.UtcNow;
        credential.RuntimeState = "pending-revoke";
        credential.LastError = string.Empty;
        _ = await db.SaveChangesAsync(cancellationToken);
        await signals.WriteSmbRevokeAsync(credential.Id, credential.Username, credential.UnixUser, cancellationToken);
        signals.RequestSmbApply();
        return Ok(CredentialResponse(credential, includePassword: null));
    }

    [Authorize(Policy = AuthorizationPolicies.Automation)]
    [HttpGet("config/smb.conf")]
    public async Task<IActionResult> SmbConf(CancellationToken cancellationToken)
    {
        var shares = await db.SmbShares.AsNoTracking().Where(s => s.Enabled).ToListAsync(cancellationToken);
        var credentials = await db.SmbCredentials
            .AsNoTracking()
            .Where(c => c.Enabled && c.RevokedAt == null)
            .ToListAsync(cancellationToken);
        return Content(config.BuildSmbConf(shares, credentials), "text/plain");
    }

    [Authorize(Policy = AuthorizationPolicies.Automation)]
    [HttpGet("reconcile/desired")]
    public async Task<IActionResult> Desired(CancellationToken cancellationToken)
    {
        var shares = await db.SmbShares.AsNoTracking().OrderBy(s => s.ShareName).ToListAsync(cancellationToken);
        var credentials = await db.SmbCredentials.AsNoTracking().OrderBy(c => c.UnixUser).ToListAsync(cancellationToken);
        return Ok(new
        {
            shares,
            credentials = credentials.Select(c => CredentialResponse(c, includePassword: null))
        });
    }

    [Authorize(Policy = AuthorizationPolicies.Automation)]
    [HttpPost("reconcile/result")]
    public async Task<IActionResult> ApplyResult([FromBody] SmbApplyResultRequest request, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var shareResult in request.Shares ?? [])
        {
            var share = await db.SmbShares.FindAsync([shareResult.Id], cancellationToken);
            if (share is null) continue;
            share.RuntimeState = string.IsNullOrWhiteSpace(shareResult.State) ? "applied" : shareResult.State.Trim();
            share.LastError = shareResult.Error ?? string.Empty;
            share.LastAppliedAt = now;
        }

        foreach (var credentialResult in request.Credentials ?? [])
        {
            var credential = await db.SmbCredentials.FindAsync([credentialResult.Id], cancellationToken);
            if (credential is null) continue;
            credential.RuntimeState = string.IsNullOrWhiteSpace(credentialResult.State) ? "applied" : credentialResult.State.Trim();
            credential.LastError = credentialResult.Error ?? string.Empty;
            credential.LastAppliedAt = now;
        }

        _ = await db.SaveChangesAsync(cancellationToken);
        return Ok(new { appliedAt = now });
    }

    private async Task<SmbShareEntity> EnsureShareAsync(
        Guid familyId,
        string? name,
        string? requestedShareName,
        bool readOnly,
        bool enabled,
        CancellationToken cancellationToken)
    {
        var shareName = NormalizeShareName(string.IsNullOrWhiteSpace(requestedShareName)
            ? $"homeharbor-{familyId:N}"[..19]
            : requestedShareName);
        var share = await db.SmbShares.FirstOrDefaultAsync(
            s => s.FamilyId == familyId && s.ShareName == shareName,
            cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var path = Path.GetFullPath(Path.Combine(storage.DataRoot, "families", familyId.ToString("N")));
        _ = Directory.CreateDirectory(path);

        if (share is null)
        {
            share = new SmbShareEntity
            {
                Id = Guid.NewGuid(),
                FamilyId = familyId,
                ShareName = shareName,
                CreatedAt = now
            };
            _ = db.SmbShares.Add(share);
        }

        share.Name = string.IsNullOrWhiteSpace(name) ? "HomeHarbor family data" : name.Trim();
        share.Path = path;
        share.ReadOnly = readOnly;
        share.Enabled = enabled;
        share.RuntimeState = "pending";
        share.LastError = string.Empty;
        share.UpdatedAt = now;
        return share;
    }

    private static string NormalizeShareName(string value)
    {
        var shareName = value.Trim().ToLowerInvariant();
        return !ShareNameRegex().IsMatch(shareName)
            ? throw new InvalidOperationException("ShareName must contain only letters, numbers, underscore, or dash.")
            : shareName is "ipc$" or "admin$" ? throw new InvalidOperationException("This SMB share name is reserved.") : shareName;
    }

    private async Task<string?> AllocateUnixUserAsync(CancellationToken cancellationToken)
    {
        var activeUsers = await db.SmbCredentials
            .AsNoTracking()
            .Where(c => c.RevokedAt == null)
            .Select(c => c.UnixUser)
            .ToListAsync(cancellationToken);
        var used = activeUsers.ToHashSet(StringComparer.Ordinal);
        for (var index = 1; index <= SmbUserPoolSize; index++)
        {
            var user = $"homeharbor-smb{index:000}";
            if (!used.Contains(user)) return user;
        }

        return null;
    }

    private async Task<int> ActiveCredentialCountAsync(Guid shareId, CancellationToken cancellationToken)
        => await db.SmbCredentials.AsNoTracking().CountAsync(c => c.ShareId == shareId && c.RevokedAt == null, cancellationToken);

    private static object ShareResponse(SmbShareEntity share, int credentialCount)
        => new
        {
            share.Id,
            share.FamilyId,
            share.Name,
            share.ShareName,
            share.Path,
            share.ReadOnly,
            share.Enabled,
            share.RuntimeState,
            share.LastError,
            share.CreatedAt,
            share.UpdatedAt,
            share.LastAppliedAt,
            credentialCount,
            unc = $@"\\homeharbor\{share.ShareName}"
        };

    private static object CredentialResponse(SmbCredentialEntity credential, string? includePassword)
        => new
        {
            credential.Id,
            credential.FamilyId,
            credential.ShareId,
            credential.DisplayName,
            credential.Username,
            credential.UnixUser,
            credential.ReadOnly,
            credential.Enabled,
            credential.RuntimeState,
            credential.LastError,
            credential.CreatedAt,
            credential.RotatedAt,
            credential.RevokedAt,
            credential.LastAppliedAt,
            password = includePassword
        };

    [GeneratedRegex("^[a-z0-9_-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex ShareNameRegex();

    public sealed record CreateSmbShareRequest(Guid? FamilyId, string? Name, string? ShareName, bool? ReadOnly, bool? Enabled);
    public sealed record UpdateSmbShareRequest(string? Name, bool? ReadOnly, bool? Enabled);
    public sealed record CreateSmbCredentialRequest(Guid? FamilyId, Guid? ShareId, string? DisplayName, bool? ReadOnly);
    public sealed record SmbApplyResultRequest(IReadOnlyList<SmbItemResult>? Shares, IReadOnlyList<SmbItemResult>? Credentials);
    public sealed record SmbItemResult(Guid Id, string? State, string? Error);
}
