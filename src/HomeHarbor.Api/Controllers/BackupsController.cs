using HomeHarbor.Api.Data;
using HomeHarbor.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HomeHarbor.Api.Controllers;

[ApiController]
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
        return Ok(targets.Select(t => new
        {
            t.Id,
            t.FamilyId,
            t.Name,
            t.RepositoryUri,
            t.EncryptionEnabled,
            t.CreatedAt,
            t.LastVerifiedAt
        }));
    }

    [HttpPost("targets")]
    public async Task<IActionResult> CreateTarget([FromBody] CreateBackupTargetRequest request, CancellationToken cancellationToken)
    {
        var familyId = await families.ResolveAsync(request.FamilyId, cancellationToken);
        if (familyId is null) return BadRequest(new { error = "Create a family space first." });
        if (string.IsNullOrWhiteSpace(request.RepositoryUri)) return BadRequest(new { error = "RepositoryUri is required." });

        var target = new BackupTargetEntity
        {
            Id = Guid.NewGuid(),
            FamilyId = familyId.Value,
            Name = string.IsNullOrWhiteSpace(request.Name) ? "External backup" : request.Name.Trim(),
            RepositoryUri = request.RepositoryUri.Trim(),
            EncryptionEnabled = true,
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
            restoreDrill = $"restic -r {target.RepositoryUri} snapshots && restic -r {target.RepositoryUri} restore latest --target /tmp/homeharbor-restore"
        });
    }

    [HttpPost("targets/{id:guid}/verify")]
    public async Task<IActionResult> VerifyTarget(Guid id, CancellationToken cancellationToken)
    {
        var target = await db.BackupTargets.FindAsync([id], cancellationToken);
        if (target is null) return NotFound(new { error = "Backup target not found." });
        await families.RequireAccessAsync(target.FamilyId, cancellationToken);

        target.LastVerifiedAt = DateTimeOffset.UtcNow;
        _ = await db.SaveChangesAsync(cancellationToken);
        return Ok(new
        {
            target.Id,
            target.LastVerifiedAt,
            command = $"restic -r {target.RepositoryUri} check",
            result = "Verification planned. Appliance runner executes this command with RESTIC_PASSWORD from sealed local settings."
        });
    }

    [HttpGet("jobs")]
    public async Task<IActionResult> Jobs([FromQuery] Guid? familyId, CancellationToken cancellationToken)
    {
        var resolved = await families.ResolveAsync(familyId, cancellationToken);
        return resolved is null
            ? BadRequest(new { error = "Create a family space first." })
            : Ok(await db.BackupJobs
            .AsNoTracking()
            .Where(j => j.FamilyId == resolved.Value)
            .OrderByDescending(j => j.StartedAt)
            .Take(50)
            .ToListAsync(cancellationToken));
    }

    [HttpPost("run")]
    public async Task<IActionResult> Run([FromBody] RunBackupRequest request, CancellationToken cancellationToken)
    {
        var target = await db.BackupTargets.AsNoTracking().FirstOrDefaultAsync(t => t.Id == request.BackupTargetId, cancellationToken);
        if (target is null) return NotFound(new { error = "Backup target not found." });
        await families.RequireAccessAsync(target.FamilyId, cancellationToken);
        var job = CreatePlannedJob(target);

        _ = db.BackupJobs.Add(job);
        _ = await db.SaveChangesAsync(cancellationToken);
        return Ok(job);
    }

    [HttpPost("one-click")]
    public async Task<IActionResult> OneClick([FromBody] OneClickBackupRequest request, CancellationToken cancellationToken)
    {
        var resolved = await families.ResolveAsync(request.FamilyId, cancellationToken);
        if (resolved is null) return BadRequest(new { error = "Create a family space first." });

        BackupTargetEntity? target = null;
        if (request.BackupTargetId is { } targetId)
        {
            target = await db.BackupTargets.AsNoTracking().FirstOrDefaultAsync(
                t => t.Id == targetId && t.FamilyId == resolved.Value,
                cancellationToken);
            if (target is null) return NotFound(new { error = "Backup target not found." });
        }

        if (target is null)
        {
            var repository = string.IsNullOrWhiteSpace(request.RepositoryUri)
                ? $"file:///mnt/homeharbor-backup/{resolved.Value:N}"
                : request.RepositoryUri.Trim();
            target = new BackupTargetEntity
            {
                Id = Guid.NewGuid(),
                FamilyId = resolved.Value,
                Name = string.IsNullOrWhiteSpace(request.Name) ? "One-click external backup" : request.Name.Trim(),
                RepositoryUri = repository,
                EncryptionEnabled = true,
                CreatedAt = DateTimeOffset.UtcNow,
                LastVerifiedAt = DateTimeOffset.UtcNow
            };
            _ = db.BackupTargets.Add(target);
        }

        var job = CreatePlannedJob(target);
        _ = db.BackupJobs.Add(job);
        _ = await db.SaveChangesAsync(cancellationToken);

        return Ok(new
        {
            target = new
            {
                target.Id,
                target.Name,
                target.RepositoryUri,
                target.EncryptionEnabled,
                target.LastVerifiedAt
            },
            job,
            restoreDrill = $"restic -r {target.RepositoryUri} restore latest --target /tmp/homeharbor-restore-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}"
        });
    }

    private static BackupJobEntity CreatePlannedJob(BackupTargetEntity target)
        => new()
        {
            Id = Guid.NewGuid(),
            FamilyId = target.FamilyId,
            BackupTargetId = target.Id,
            State = "planned",
            Command = $"restic -r {target.RepositoryUri} backup /homeharbor-data/families/{target.FamilyId:N}",
            Result = "Backup job created. Appliance runner executes this command with RESTIC_PASSWORD from sealed local settings.",
            StartedAt = DateTimeOffset.UtcNow
        };

    public sealed record CreateBackupTargetRequest(Guid? FamilyId, string? Name, string RepositoryUri);
    public sealed record RunBackupRequest(Guid BackupTargetId);
    public sealed record OneClickBackupRequest(Guid? FamilyId, Guid? BackupTargetId, string? Name, string? RepositoryUri);
}
