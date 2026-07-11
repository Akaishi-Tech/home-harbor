using HomeHarbor.Api.Auth;
using HomeHarbor.Api.Data;
using HomeHarbor.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HomeHarbor.Api.Controllers;

[ApiController]
[AllowAnonymous]
[Route("api/setup/storage")]
public sealed class SetupStorageController(
    IServiceProvider services,
    IStorageOobeService storageOobe,
    ISetupPairingService pairings,
    IAuthorizationService authorization) : ControllerBase
{
    public const string SetupCodeHeader = "X-HomeHarbor-Setup-Code";
    private static readonly SemaphoreSlim MutationGate = new(1, 1);

    [HttpGet("inventory")]
    public async Task<IActionResult> Inventory(CancellationToken cancellationToken)
    {
        var denied = await DenySensitiveReadAsync(cancellationToken);
        return denied ?? Ok(await storageOobe.InventoryAsync(cancellationToken));
    }

    [HttpPost("recommendation")]
    public async Task<IActionResult> Recommendation(
        [FromBody] StorageUseProfile profile,
        CancellationToken cancellationToken)
    {
        var denied = await DenySensitiveReadAsync(cancellationToken);
        if (denied is not null) return denied;
        var inventory = await storageOobe.InventoryAsync(cancellationToken);
        return Ok(storageOobe.Recommend(inventory, profile));
    }

    [HttpPost("plan")]
    public async Task<IActionResult> Plan([FromBody] StoragePlanRequest request, CancellationToken cancellationToken)
    {
        await MutationGate.WaitAsync(cancellationToken);
        try
        {
            if (await IsInitializedAsync(cancellationToken))
                return Conflict(new { error = "Storage plans can only be created before HomeHarbor is initialized." });
            if (!pairings.IsBootstrapCodeValid(request.PairingCode))
                return Unauthorized(new { error = "A valid appliance setup code is required." });

            var inventory = await storageOobe.InventoryAsync(cancellationToken);
            return Ok(await storageOobe.CreatePlanAsync(inventory, request, cancellationToken));
        }
        finally
        {
            _ = MutationGate.Release();
        }
    }

    [HttpPost("apply")]
    public async Task<IActionResult> Apply([FromBody] StorageApplyRequest request, CancellationToken cancellationToken)
    {
        await MutationGate.WaitAsync(cancellationToken);
        try
        {
            if (await IsInitializedAsync(cancellationToken))
                return Conflict(new { error = "Storage plans can only be applied before HomeHarbor is initialized." });
            if (!pairings.IsBootstrapCodeValid(request.PairingCode))
                return Unauthorized(new { error = "A valid appliance setup code is required." });

            return Ok(await storageOobe.ApplyAsync(
                request.PlanId,
                request.Confirmation,
                request.RecoveryPassphrase,
                cancellationToken));
        }
        finally
        {
            _ = MutationGate.Release();
        }
    }

    [HttpGet("status")]
    public async Task<IActionResult> Status(CancellationToken cancellationToken)
    {
        var denied = await DenySensitiveReadAsync(cancellationToken);
        return denied ?? Ok(await storageOobe.StatusAsync(cancellationToken));
    }

    private async Task<IActionResult?> DenySensitiveReadAsync(CancellationToken cancellationToken)
    {
        if (await IsInitializedAsync(cancellationToken))
        {
            if ((await authorization.AuthorizeAsync(User, null, AuthorizationPolicies.FamilyAdmin)).Succeeded) return null;
            return User.Identity?.IsAuthenticated == true ? Forbid() : Unauthorized();
        }

        return pairings.IsBootstrapCodeValid(Request.Headers[SetupCodeHeader].ToString())
            ? null
            : Unauthorized(new { error = "A valid appliance setup code is required." });
    }

    private async Task<bool> IsInitializedAsync(CancellationToken cancellationToken)
    {
        if (pairings.IsBootstrapComplete()) return true;
        if (!await storageOobe.IsReadyAsync(cancellationToken)) return false;
        var db = services.GetRequiredService<HomeHarborDbContext>();
        return await db.FamilySpaces.AsNoTracking().AnyAsync(cancellationToken);
    }
}
