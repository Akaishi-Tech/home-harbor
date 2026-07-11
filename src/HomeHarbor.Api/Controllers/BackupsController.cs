using HomeHarbor.Api.Auth;
using HomeHarbor.Api.Data;
using HomeHarbor.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HomeHarbor.Api.Controllers;

[ApiController]
[Authorize(Policy = AuthorizationPolicies.FamilyMember)]
[Route("api/backups")]
public sealed class BackupsController(HomeHarborDbContext db, IFamilyResolver families) : ControllerBase
{
    [HttpGet("targets")]
    public async Task<IActionResult> Targets([FromQuery] Guid? familyId, CancellationToken cancellationToken)
    {
        var resolved = await families.ResolveAsync(familyId, cancellationToken);
        if (resolved is null) return BadRequest(new { error = "Create a family space first." });

        var targets = await db.BackupTargets
            .AsNoTracking()
            .Where(t => t.FamilyId == resolved.Value)
            .OrderByDescending(t => t.CreatedAt)
            .ToListAsync(cancellationToken);
        var mayManage = User.IsInRole(HomeHarbor.Core.Identity.FamilyRoles.Owner) ||
            User.IsInRole(HomeHarbor.Core.Identity.FamilyRoles.Admin);
        return Ok(targets.Select(t => new
        {
            t.Id,
            t.FamilyId,
            t.Name,
            repositoryUri = mayManage ? t.RepositoryUri : null,
            t.EncryptionEnabled,
            t.CreatedAt,
            t.LastVerifiedAt
        }));
    }

    [HttpPost("targets")]
    [Authorize(Policy = AuthorizationPolicies.FamilyAdmin)]
    public async Task<IActionResult> CreateTarget([FromBody] CreateBackupTargetRequest request, CancellationToken cancellationToken)
    {
        var familyId = await families.ResolveAsync(request.FamilyId, cancellationToken);
        if (familyId is null) return BadRequest(new { error = "Create a family space first." });
        if (!TryNormalizeRepositoryUri(request.RepositoryUri, out var repositoryUri, out var repositoryError))
            return BadRequest(new { error = repositoryError });
        if (request.Name?.Trim().Length > 96)
            return BadRequest(new { error = "Name must not exceed 96 characters." });

        var target = new BackupTargetEntity
        {
            Id = Guid.NewGuid(),
            FamilyId = familyId.Value,
            Name = string.IsNullOrWhiteSpace(request.Name) ? "External backup" : request.Name.Trim(),
            RepositoryUri = repositoryUri,
            EncryptionEnabled = false,
            CreatedAt = DateTimeOffset.UtcNow
        };
        _ = db.BackupTargets.Add(target);
        _ = await db.SaveChangesAsync(cancellationToken);

        return Ok(new
        {
            target.Id,
            target.FamilyId,
            target.Name,
            target.RepositoryUri,
            target.EncryptionEnabled,
            target.CreatedAt,
            capability = "configured-only; backup runner and repository encryption are unavailable"
        });
    }

    [HttpPost("targets/{id:guid}/verify")]
    [Authorize(Policy = AuthorizationPolicies.FamilyAdmin)]
    public IActionResult VerifyTarget(Guid id)
    {
        _ = id;
        return StatusCode(StatusCodes.Status501NotImplemented, new
        {
            error = "Backup verification is unavailable because no privileged backup runner is installed."
        });
    }

    [HttpGet("jobs")]
    public async Task<IActionResult> Jobs([FromQuery] Guid? familyId, CancellationToken cancellationToken)
    {
        var resolved = await families.ResolveAsync(familyId, cancellationToken);
        var mayManage = User.IsInRole(HomeHarbor.Core.Identity.FamilyRoles.Owner) ||
            User.IsInRole(HomeHarbor.Core.Identity.FamilyRoles.Admin);
        return resolved is null
            ? BadRequest(new { error = "Create a family space first." })
            : Ok(await db.BackupJobs
            .AsNoTracking()
            .Where(j => j.FamilyId == resolved.Value)
            .OrderByDescending(j => j.StartedAt)
            .Take(50)
            .Select(j => new
            {
                j.Id,
                j.FamilyId,
                j.BackupTargetId,
                j.State,
                command = mayManage ? j.Command : null,
                result = mayManage ? j.Result : null,
                j.StartedAt,
                j.FinishedAt
            })
            .ToListAsync(cancellationToken));
    }

    [HttpPost("run")]
    [Authorize(Policy = AuthorizationPolicies.FamilyAdmin)]
    public IActionResult Run([FromBody] RunBackupRequest request)
    {
        _ = request;
        return StatusCode(StatusCodes.Status501NotImplemented, new
        {
            error = "Backup execution is unavailable because no privileged backup runner is installed."
        });
    }

    [HttpPost("one-click")]
    [Authorize(Policy = AuthorizationPolicies.FamilyAdmin)]
    public IActionResult OneClick([FromBody] OneClickBackupRequest request)
    {
        _ = request;
        return StatusCode(StatusCodes.Status501NotImplemented, new
        {
            error = "One-click backup is unavailable because no privileged backup runner is installed."
        });
    }

    public sealed record CreateBackupTargetRequest(Guid? FamilyId, string? Name, string RepositoryUri);
    public sealed record RunBackupRequest(Guid BackupTargetId);
    public sealed record OneClickBackupRequest(Guid? FamilyId, Guid? BackupTargetId, string? Name, string? RepositoryUri);

    internal static bool TryNormalizeRepositoryUri(string? value, out string repositoryUri, out string error)
    {
        repositoryUri = value?.Trim() ?? string.Empty;
        if (repositoryUri.Length == 0)
        {
            error = "RepositoryUri is required.";
            return false;
        }

        if (repositoryUri.Length > 1024 ||
            repositoryUri.Any(char.IsControl) ||
            repositoryUri.Any(char.IsWhiteSpace) ||
            !Uri.TryCreate(repositoryUri, UriKind.Absolute, out var parsedRepository) ||
            parsedRepository.Scheme is not ("file" or "sftp" or "s3" or "rest" or "https"))
        {
            error = "RepositoryUri must be a valid file, sftp, s3, rest, or https URI without whitespace.";
            return false;
        }

        if (!string.IsNullOrEmpty(parsedRepository.UserInfo) ||
            !string.IsNullOrEmpty(parsedRepository.Query) ||
            !string.IsNullOrEmpty(parsedRepository.Fragment))
        {
            error = "RepositoryUri must not embed credentials, query parameters, or fragments.";
            return false;
        }

        if (parsedRepository.IsFile)
        {
            if (!string.IsNullOrEmpty(parsedRepository.Host))
            {
                error = "file repositories must not specify a host.";
                return false;
            }

            try
            {
                var backupRoot = Path.GetFullPath("/mnt/homeharbor-backup");
                var localPath = Path.GetFullPath(parsedRepository.LocalPath);
                if (!string.Equals(localPath, backupRoot, StringComparison.Ordinal) &&
                    !localPath.StartsWith(backupRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                {
                    error = "file repositories must stay at or under /mnt/homeharbor-backup/.";
                    return false;
                }
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                error = "file repository path is invalid.";
                return false;
            }
        }

        error = string.Empty;
        return true;
    }
}
