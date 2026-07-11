using System.Text.Json;
using HomeHarbor.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace HomeHarbor.Api.Controllers;

[ApiController]
[Route("api/security")]
public sealed class SecurityController(
    IHomeHarborStorageService storage,
    IOptions<HomeHarborRuntimeOptions> runtimeOptions) : ControllerBase
{
    private readonly HomeHarborRuntimeOptions _runtimeOptions = runtimeOptions.Value;

    [HttpGet("policy")]
    public IActionResult Policy()
        => Ok(new
        {
            localFirst = true,
            storage = new
            {
                mode = "local-default",
                dataRoot = storage.DataRoot,
                applianceAtRestEncryption = "luks2-btrfs-data-partition",
                unlockMode = ReadDataUnlockMode(_runtimeOptions.DataUnlockMetadataPath)
            },
            endToEndEncryption = new
            {
                requiredByDefault = false,
                vault = "client-encrypted-payloads-only",
                filesAndPhotos = "server-readable-webdav-and-smb-content",
                serverPlaintextPolicy = "vault-payloads-are-client-encrypted; file-and-photo-content-is-protected-at-rest-only"
            },
            backups = new
            {
                oneClickExternal = false,
                tool = "restic",
                encryptedRepository = "planned-not-executed-by-the-current-control-plane"
            },
            identity = new
            {
                localFamilyMembers = true,
                deviceScopedWebDavTokens = true,
                tokenStorage = "bcrypt-hash-at-rest"
            }
        });

    private static string ReadDataUnlockMode(string metadataPath)
    {
        try
        {
            if (System.IO.File.Exists(metadataPath))
            {
                using var stream = System.IO.File.OpenRead(metadataPath);
                using var document = JsonDocument.Parse(stream);
                if (document.RootElement.TryGetProperty("unlockMode", out var unlockModeElement))
                {
                    var unlockMode = unlockModeElement.GetString();
                    if (unlockMode is "passphrase" or "tpm2")
                    {
                        return unlockMode;
                    }

                    if (!string.IsNullOrWhiteSpace(unlockMode))
                    {
                        return "unsupported";
                    }
                }
            }
        }
        catch (IOException)
        {
        }
        catch (JsonException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return "passphrase";
    }
}
