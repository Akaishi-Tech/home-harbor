using HomeHarbor.Api.Services;
using HomeHarbor.Api.Auth;
using HomeHarbor.Core.Ota;
using HomeHarbor.Tooling;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HomeHarbor.Api.Controllers;

[ApiController]
[Route("api/ota")]
public sealed class OtaController : ControllerBase
{
    [HttpGet("status")]
    public IActionResult Status()
        => Ok(new
        {
            version = OtaRuntime.Version(),
            channel = OtaRuntime.Channel(),
            availableChannels = OtaRuntime.AvailableChannels(),
            kernelChannel = OtaRuntime.KernelChannelName(),
            availableKernelChannels = OtaRuntime.AvailableKernelChannels(),
            updateState = OtaRuntime.UpdateState(),
            stageEndpoint = "/api/ota/stage",
            applyEndpoint = "/api/ota/apply"
        });

    [HttpPost("apply")]
    [Authorize(Policy = AuthorizationPolicies.FamilyAdmin)]
    public IActionResult Apply([FromBody] OtaManifest manifest)
    {
        _ = manifest;
        return StatusCode(StatusCodes.Status501NotImplemented, new
        {
            error = "OTA apply is unavailable until the appliance updater pipeline is connected. No update was scheduled."
        });
    }

    [HttpPost("stage")]
    [Authorize(Policy = AuthorizationPolicies.FamilyAdmin)]
    public IActionResult Stage([FromBody] OtaManifest manifest)
    {
        _ = manifest;
        return StatusCode(StatusCodes.Status501NotImplemented, new
        {
            error = "OTA staging is unavailable until the appliance updater pipeline is connected. No update was staged."
        });
    }
}
