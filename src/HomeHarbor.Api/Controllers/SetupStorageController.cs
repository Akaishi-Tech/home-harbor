using HomeHarbor.Api.Data;
using HomeHarbor.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HomeHarbor.Api.Controllers;

[ApiController]
[AllowAnonymous]
[Route("api/setup/storage")]
public sealed class SetupStorageController(IServiceProvider services, IStorageOobeService storageOobe) : ControllerBase
{
    [HttpGet("inventory")]
    public async Task<IActionResult> Inventory(CancellationToken cancellationToken)
        => Ok(await storageOobe.InventoryAsync(cancellationToken));

    [HttpPost("recommendation")]
    public async Task<IActionResult> Recommendation(
        [FromBody] StorageUseProfile profile,
        CancellationToken cancellationToken)
    {
        var inventory = await storageOobe.InventoryAsync(cancellationToken);
        return Ok(storageOobe.Recommend(inventory, profile));
    }

    [HttpPost("plan")]
    public async Task<IActionResult> Plan([FromBody] StoragePlanRequest request, CancellationToken cancellationToken)
    {
        if (await IsInitializedAsync(cancellationToken))
        {
            return Conflict(new { error = "Storage plans can only be created before HomeHarbor is initialized." });
        }

        var inventory = await storageOobe.InventoryAsync(cancellationToken);
        return Ok(await storageOobe.CreatePlanAsync(inventory, request, cancellationToken));
    }

    [HttpPost("apply")]
    public async Task<IActionResult> Apply([FromBody] StorageApplyRequest request, CancellationToken cancellationToken)
    {
        return await IsInitializedAsync(cancellationToken)
            ? Conflict(new { error = "Storage plans can only be applied before HomeHarbor is initialized." })
            : Ok(await storageOobe.ApplyAsync(request.PlanId, request.Confirmation, request.RecoveryPassphrase, cancellationToken));
    }

    [HttpGet("status")]
    public async Task<IActionResult> Status(CancellationToken cancellationToken)
        => Ok(await storageOobe.StatusAsync(cancellationToken));

    private Task<bool> IsInitializedAsync(CancellationToken cancellationToken)
        => IsInitializedCoreAsync(cancellationToken);

    private async Task<bool> IsInitializedCoreAsync(CancellationToken cancellationToken)
    {
        if (!await storageOobe.IsReadyAsync(cancellationToken))
        {
            return false;
        }

        var db = services.GetRequiredService<HomeHarborDbContext>();
        return await db.FamilySpaces.AsNoTracking().AnyAsync(cancellationToken);
    }
}
