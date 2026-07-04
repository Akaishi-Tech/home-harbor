using HomeHarbor.Api.Data;
using HomeHarbor.Api.Services;
using HomeHarbor.Core.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QRCoder;

namespace HomeHarbor.Api.Controllers;

[ApiController]
[AllowAnonymous]
[Route("api/setup")]
public sealed class SetupController(
    IServiceProvider services,
    IHomeHarborStorageService storage,
    IStorageOobeService storageOobe,
    ITokenGenerator tokenGenerator,
    IJwtTokenService jwtTokens,
    ISetupPairingService pairings) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Status(CancellationToken cancellationToken)
    {
        var storageStatus = await storageOobe.StatusAsync(cancellationToken);
        FamilySpaceEntity? family = null;
        if (storageStatus.State == StorageApplyState.Succeeded)
        {
            var db = services.GetRequiredService<HomeHarborDbContext>();
            family = await db.FamilySpaces.AsNoTracking().OrderBy(f => f.CreatedAt).FirstOrDefaultAsync(cancellationToken);
        }

        return Ok(new
        {
            initialized = family is not null,
            family = family is null ? null : new { family.Id, family.Name, family.OwnerDisplayName, family.CreatedAt },
            onboarding = new
            {
                pairing = "/api/setup/pairing",
                qr = "/api/setup/pairing.svg",
                steps = new[] { "storage-profile", "storage-plan", "apply-storage", "create-family-space", "configure-services", "sync-devices" }
            },
            storage = new
            {
                status = storageStatus.State,
                storageStatus.Progress,
                storageStatus.Message,
                storageStatus.Error,
                storageStatus.PlanId,
                storageStatus.UpdatedAt,
                endpoints = new
                {
                    inventory = "/api/setup/storage/inventory",
                    recommendation = "/api/setup/storage/recommendation",
                    plan = "/api/setup/storage/plan",
                    apply = "/api/setup/storage/apply",
                    status = "/api/setup/storage/status"
                }
            }
        });
    }

    [HttpGet("pairing")]
    public async Task<IActionResult> Pairing(CancellationToken cancellationToken)
    {
        var storageReady = await storageOobe.IsReadyAsync(cancellationToken);
        var initialized = storageReady && await IsInitializedAsync(cancellationToken);
        var ticket = pairings.GetOrCreate(BuildPublicOrigin());
        return Ok(new
        {
            initialized,
            ticket.Code,
            ticket.PairingUrl,
            ticket.ExpiresAt,
            qrSvg = "/api/setup/pairing.svg",
            qrPayload = ticket.PairingUrl,
            setup = new
            {
                method = "scan-or-open-url",
                api = "/api/setup",
                expiresInSeconds = Math.Max(0, (int)(ticket.ExpiresAt - DateTimeOffset.UtcNow).TotalSeconds)
            }
        });
    }

    [HttpGet("pairing.svg")]
    public IActionResult PairingSvg()
    {
        var ticket = pairings.GetOrCreate(BuildPublicOrigin());
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(ticket.PairingUrl, QRCodeGenerator.ECCLevel.Q);
        var qr = new SvgQRCode(data);
        var svg = qr.GetGraphic(5, "#10231e", "#ffffff", drawQuietZones: true);
        return Content(svg, "image/svg+xml");
    }

    [HttpPost]
    public async Task<IActionResult> CreateFamily([FromBody] CreateFamilyRequest request, CancellationToken cancellationToken)
    {
        if (!await storageOobe.IsReadyAsync(cancellationToken))
        {
            return Conflict(new { error = "Complete encrypted storage setup before creating a family space." });
        }

        var db = services.GetRequiredService<HomeHarborDbContext>();
        var existing = await db.FamilySpaces.AsNoTracking().AnyAsync(cancellationToken);
        if (existing) return Conflict(new { error = "HomeHarbor has already been initialized." });
        if (!pairings.IsValid(request.PairingCode))
            return BadRequest(new { error = "Initialization credential is expired or invalid. Please retry setup." });

        var now = DateTimeOffset.UtcNow;
        var family = new FamilySpaceEntity
        {
            Id = Guid.NewGuid(),
            Name = string.IsNullOrWhiteSpace(request.FamilyName) ? "Home" : request.FamilyName.Trim(),
            OwnerDisplayName = string.IsNullOrWhiteSpace(request.OwnerDisplayName) ? "Owner" : request.OwnerDisplayName.Trim(),
            CreatedAt = now
        };
        var device = new DeviceEntity
        {
            Id = Guid.NewGuid(),
            FamilyId = family.Id,
            DisplayName = string.IsNullOrWhiteSpace(request.DeviceName) ? "First device" : request.DeviceName.Trim(),
            Kind = "browser",
            CreatedAt = now
        };
        var owner = new FamilyMemberEntity
        {
            Id = Guid.NewGuid(),
            FamilyId = family.Id,
            DisplayName = family.OwnerDisplayName,
            Role = FamilyRoles.Owner,
            PasswordHash = string.IsNullOrWhiteSpace(request.OwnerPassword)
                ? null
                : BCrypt.Net.BCrypt.HashPassword(request.OwnerPassword),
            CreatedAt = now
        };
        var plaintext = tokenGenerator.GenerateSecret();
        var token = new WebDavTokenEntity
        {
            Id = Guid.NewGuid(),
            FamilyId = family.Id,
            DeviceId = device.Id,
            Username = tokenGenerator.GenerateUsername(),
            TokenHash = BCrypt.Net.BCrypt.HashPassword(plaintext),
            Scope = WebDavTokenScope.All,
            Description = "Initial setup token",
            CreatedAt = now
        };

        _ = db.FamilySpaces.Add(family);
        _ = db.Devices.Add(device);
        _ = db.FamilyMembers.Add(owner);
        _ = db.WebDavTokens.Add(token);
        var tokenId = jwtTokens.GenerateTokenId();
        var session = new MemberSessionEntity
        {
            Id = Guid.NewGuid(),
            FamilyId = family.Id,
            MemberId = owner.Id,
            TokenHash = JwtTokenService.HashTokenId(tokenId),
            CreatedAt = now,
            ExpiresAt = now.Add(jwtTokens.UserAccessTokenLifetime)
        };
        _ = db.MemberSessions.Add(session);
        _ = await db.SaveChangesAsync(cancellationToken);
        storage.EnsureFamilyRoots(family.Id);
        pairings.Consume(request.PairingCode);

        var recoveryCode = tokenGenerator.GenerateRecoveryCode();
        var keyHint = $"family-{family.Id:N}-v1";

        return Ok(new
        {
            family = new { family.Id, family.Name, family.OwnerDisplayName, family.CreatedAt },
            device = new { device.Id, device.DisplayName, device.Kind },
            owner = new { owner.Id, owner.DisplayName, owner.Role },
            auth = new
            {
                accessToken = jwtTokens.IssueUserAccessToken(session, owner, family, tokenId),
                tokenType = "Bearer",
                session.ExpiresAt,
                member = new { owner.Id, owner.DisplayName, owner.Role },
                family = new { family.Id, family.Name, family.OwnerDisplayName, family.CreatedAt }
            },
            recoveryCode,
            webDav = new { token.Username, token = plaintext, token.Scope },
            encryption = new
            {
                mode = "client-side-e2ee",
                localStorage = "default",
                atRest = "appliance-data-luks2",
                keyHint,
                recoveryCode
            }
        });
    }

    private string BuildPublicOrigin()
    {
        var scheme = Request.Headers.TryGetValue("X-Forwarded-Proto", out var forwardedProto)
            ? forwardedProto.ToString().Split(',')[0].Trim()
            : Request.Scheme;
        var host = Request.Headers.TryGetValue("X-Forwarded-Host", out var forwardedHost)
            ? forwardedHost.ToString().Split(',')[0].Trim()
            : Request.Host.Value;
        return $"{scheme}://{host}";
    }

    private async Task<bool> IsInitializedAsync(CancellationToken cancellationToken)
    {
        var db = services.GetRequiredService<HomeHarborDbContext>();
        return await db.FamilySpaces.AsNoTracking().AnyAsync(cancellationToken);
    }

    public sealed record CreateFamilyRequest(
        string? FamilyName,
        string? OwnerDisplayName,
        string? DeviceName,
        string? OwnerPassword,
        string? PairingCode);
}
