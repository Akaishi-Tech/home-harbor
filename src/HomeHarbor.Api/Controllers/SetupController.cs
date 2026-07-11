using System.Data.Common;
using HomeHarbor.Api.Auth;
using HomeHarbor.Api.Data;
using HomeHarbor.Api.Services;
using HomeHarbor.Core.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using QRCoder;

namespace HomeHarbor.Api.Controllers;

[ApiController]
[AllowAnonymous]
[Route("api/setup")]
public sealed class SetupController(
    IServiceProvider services,
    IHomeHarborStorageService storage,
    IStorageHealthService storageHealth,
    IStorageOobeService storageOobe,
    ITokenGenerator tokenGenerator,
    IJwtTokenService jwtTokens,
    ISetupPairingService pairings,
    IAuthorizationService authorization,
    IOptions<HomeHarborApiOptions> apiOptions) : ControllerBase
{
    private static readonly SemaphoreSlim InitializationGate = new(1, 1);

    [HttpGet]
    public async Task<IActionResult> Status(CancellationToken cancellationToken)
    {
        var storageStatus = await storageOobe.StatusAsync(cancellationToken);
        FamilySpaceEntity? family = null;
        if (storageStatus.State == StorageApplyState.Succeeded)
        {
            try
            {
                var db = services.GetRequiredService<HomeHarborDbContext>();
                family = await db.FamilySpaces.AsNoTracking().OrderBy(f => f.CreatedAt).FirstOrDefaultAsync(cancellationToken);
            }
            catch (DbException) when (pairings.IsBootstrapComplete())
            {
                // The root-owned completion marker keeps setup closed while the database is unavailable.
            }
        }
        var initialized = pairings.IsBootstrapComplete() || family is not null;
        var mayViewFamily = initialized && await CurrentUserIsFamilyAdminAsync();

        return Ok(new
        {
            initialized,
            family = !mayViewFamily || family is null
                ? null
                : new { family.Id, family.Name, family.OwnerDisplayName, family.CreatedAt },
            onboarding = new
            {
                pairing = "/api/setup/pairing",
                qr = "/api/setup/pairing.svg",
                steps = new[] { "storage-profile", "storage-plan", "apply-storage", "create-family-space", "configure-services", "sync-devices" }
            },
            storage = new
            {
                state = storageStatus.State,
                ready = storageStatus.State == StorageApplyState.Succeeded,
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
        var initialized = await IsInitializedAsync(cancellationToken);
        if (!initialized)
        {
            return Ok(new
            {
                initialized = false,
                codeRequired = true,
                expiresAt = (DateTimeOffset?)null
            });
        }

        if (!await CurrentUserIsFamilyAdminAsync()) return PairingDenied();

        var familyId = Guid.Parse(User.FindFirst(AuthClaims.FamilyId)!.Value);
        var ticket = pairings.GetOrCreate(PublicOriginPolicy.Normalize(apiOptions.Value.PublicOrigin), familyId);
        return Ok(new
        {
            initialized = true,
            codeRequired = false,
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
    public async Task<IActionResult> PairingSvg(CancellationToken cancellationToken)
    {
        if (!await IsInitializedAsync(cancellationToken)) return NotFound();
        if (!await CurrentUserIsFamilyAdminAsync()) return PairingDenied();

        var familyId = Guid.Parse(User.FindFirst(AuthClaims.FamilyId)!.Value);
        var ticket = pairings.GetOrCreate(PublicOriginPolicy.Normalize(apiOptions.Value.PublicOrigin), familyId);
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(ticket.PairingUrl, QRCodeGenerator.ECCLevel.Q);
        var qr = new SvgQRCode(data);
        var svg = qr.GetGraphic(5, "#10231e", "#ffffff", drawQuietZones: true);
        return Content(svg, "image/svg+xml");
    }

    [HttpPost]
    public async Task<IActionResult> CreateFamily([FromBody] CreateFamilyRequest request, CancellationToken cancellationToken)
    {
        if (!TryValidateOwnerPassword(request.OwnerPassword, out var passwordError))
        {
            return BadRequest(new { error = passwordError });
        }
        if (TryFindOversizedName(request, out var oversizedField))
        {
            return BadRequest(new { error = $"{oversizedField} must not exceed 96 characters." });
        }

        await InitializationGate.WaitAsync(cancellationToken);
        try
        {
            if (pairings.IsBootstrapComplete())
                return Conflict(new { error = "HomeHarbor has already been initialized." });
            if (!await storageOobe.IsReadyAsync(cancellationToken))
                return Conflict(new { error = "Complete encrypted storage setup before creating a family space." });
            var db = services.GetRequiredService<HomeHarborDbContext>();
            if (await db.FamilySpaces.AsNoTracking().AnyAsync(cancellationToken))
                return Conflict(new { error = "HomeHarbor has already been initialized." });
            if (!pairings.IsBootstrapCodeValid(request.PairingCode))
                return BadRequest(new { error = "Initialization credential is expired or invalid. Check the code shown on the appliance." });

            var familyName = NormalizeRequiredName(request.FamilyName, "Home");
            var ownerName = NormalizeRequiredName(request.OwnerDisplayName, "Owner");
            var deviceName = NormalizeRequiredName(request.DeviceName, "First device");
            var now = DateTimeOffset.UtcNow;
            var recoveryCode = tokenGenerator.GenerateRecoveryCode();
            var family = new FamilySpaceEntity
            {
                Id = Guid.NewGuid(),
                Name = familyName,
                OwnerDisplayName = ownerName,
                RecoveryCodeHash = BCrypt.Net.BCrypt.HashPassword(recoveryCode),
                CreatedAt = now
            };
            var device = new DeviceEntity
            {
                Id = Guid.NewGuid(),
                FamilyId = family.Id,
                DisplayName = deviceName,
                Kind = "browser",
                CreatedAt = now
            };
            var owner = new FamilyMemberEntity
            {
                Id = Guid.NewGuid(),
                FamilyId = family.Id,
                DisplayName = family.OwnerDisplayName,
                Role = FamilyRoles.Owner,
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.OwnerPassword!),
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
            storage.EnsureFamilyRoots(family.Id);
            _ = db.StorageHealthSnapshots.Add(storageHealth.Check(family.Id));
            _ = await db.SaveChangesAsync(cancellationToken);
            pairings.ConsumeBootstrapCode(request.PairingCode);

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
                    mode = "vault-client-encryption-only",
                    filesAndPhotos = "server-readable; protected by appliance storage encryption at rest",
                    localStorage = "default",
                    atRest = "appliance-data-luks2",
                    keyHint
                }
            });
        }
        finally
        {
            _ = InitializationGate.Release();
        }
    }

    private async Task<bool> CurrentUserIsFamilyAdminAsync()
        => (await authorization.AuthorizeAsync(User, null, AuthorizationPolicies.FamilyAdmin)).Succeeded;

    private IActionResult PairingDenied()
        => User.Identity?.IsAuthenticated == true ? Forbid() : Unauthorized();

    private static bool TryValidateOwnerPassword(string? password, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(password) || password.Length < 12)
        {
            error = "ownerPassword must be at least 12 characters.";
            return false;
        }
        if (password.Length > 128)
        {
            error = "ownerPassword must not exceed 128 characters.";
            return false;
        }

        return true;
    }

    private static string NormalizeRequiredName(string? value, string fallback)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        return normalized;
    }

    private static bool TryFindOversizedName(CreateFamilyRequest request, out string fieldName)
    {
        (string Name, string? Value)[] names =
        [
            ("familyName", request.FamilyName),
            ("ownerDisplayName", request.OwnerDisplayName),
            ("deviceName", request.DeviceName)
        ];
        var (Name, Value) = names.FirstOrDefault(item => item.Value?.Trim().Length > 96);
        fieldName = Name ?? string.Empty;
        return fieldName.Length > 0;
    }

    private async Task<bool> IsInitializedAsync(CancellationToken cancellationToken)
    {
        if (pairings.IsBootstrapComplete()) return true;
        if (!await storageOobe.IsReadyAsync(cancellationToken)) return false;
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
