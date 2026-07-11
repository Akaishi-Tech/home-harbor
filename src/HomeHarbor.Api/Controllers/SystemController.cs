using HomeHarbor.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HomeHarbor.Api.Controllers;

[ApiController]
[Route("api/system")]
public sealed class SystemController(IStorageOobeService storageOobe) : ControllerBase
{
    [AllowAnonymous]
    [HttpGet("health")]
    public async Task<IActionResult> Health(CancellationToken cancellationToken)
    {
        var ready = await storageOobe.IsReadyAsync(cancellationToken);
        return Ok(new { status = ready ? "ok" : "storage-pending" });
    }
}
