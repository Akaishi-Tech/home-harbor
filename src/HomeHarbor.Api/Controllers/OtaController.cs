using HomeHarbor.Api.Services;
using HomeHarbor.Core.Ota;
using HomeHarbor.Tooling;
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
    public IActionResult Apply([FromBody] OtaManifest manifest)
    {
        var channel = ReleaseChannel.Require(manifest.Channel, "OTA manifest channel");
        return Accepted(new
        {
            manifest.Version,
            channel,
            kernelChannel = manifest.KernelChannel is null ? null : KernelChannel.Require(manifest.KernelChannel, "OTA manifest kernelChannel"),
            manifest.TargetSlot,
            manifest.Type,
            status = "apply-planned",
            next = "appliance OTA updater validates the bundle, writes the inactive boot slot resources and any requested root slot payloads, then reboots"
        });
    }

    [HttpPost("stage")]
    public IActionResult Stage([FromBody] OtaManifest manifest)
    {
        var channel = ReleaseChannel.Require(manifest.Channel, "OTA manifest channel");
        return Accepted(new
        {
            manifest.Version,
            channel,
            kernelChannel = manifest.KernelChannel is null ? null : KernelChannel.Require(manifest.KernelChannel, "OTA manifest kernelChannel"),
            manifest.TargetSlot,
            manifest.Type,
            status = "staged-metadata-only",
            next = "appliance OTA updater will validate the bundle and stage the next boot slot environment"
        });
    }
}
