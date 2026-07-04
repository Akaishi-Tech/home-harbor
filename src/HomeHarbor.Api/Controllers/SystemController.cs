using HomeHarbor.Api.Data;
using HomeHarbor.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HomeHarbor.Api.Controllers;

[ApiController]
[Route("api/system")]
public sealed class SystemController(
    IServiceProvider services,
    IHomeHarborStorageService storage,
    IStorageOobeService storageOobe) : ControllerBase
{
    [AllowAnonymous]
    [HttpGet("health")]
    public async Task<IActionResult> Health(CancellationToken cancellationToken)
    {
        var dataRoot = new DirectoryInfo(storage.DataRoot);
        if (!dataRoot.Exists) dataRoot.Create();

        var drive = FileSystemStats.GetDriveForPath(dataRoot.FullName);
        if (!await storageOobe.IsReadyAsync(cancellationToken))
        {
            return Ok(new
            {
                status = "storage-pending",
                initialized = false,
                storage = new
                {
                    root = dataRoot.FullName,
                    ready = false,
                    drive.TotalSize,
                    drive.AvailableFreeSpace
                },
                os = new
                {
                    version = OtaRuntime.Version(),
                    channel = OtaRuntime.Channel()
                }
            });
        }

        var db = services.GetRequiredService<HomeHarborDbContext>();
        var familyCount = await db.FamilySpaces.AsNoTracking().CountAsync(cancellationToken);
        var tokenCount = await db.WebDavTokens.AsNoTracking().CountAsync(cancellationToken);

        return Ok(new
        {
            status = "ok",
            familyCount,
            tokenCount,
            storage = new
            {
                root = dataRoot.FullName,
                drive.TotalSize,
                drive.AvailableFreeSpace
            },
            os = new
            {
                version = OtaRuntime.Version(),
                channel = OtaRuntime.Channel()
            }
        });
    }
}
