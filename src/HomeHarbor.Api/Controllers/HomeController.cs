using HomeHarbor.Api.Auth;
using HomeHarbor.Api.Data;
using HomeHarbor.Api.Services;
using HomeHarbor.Core.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HomeHarbor.Api.Controllers;

[ApiController]
[Authorize(Policy = AuthorizationPolicies.FamilyMember)]
[Route("api/home")]
public sealed class HomeController(
    HomeHarborDbContext db,
    IFamilyResolver families,
    IHomeHarborStorageService storage,
    IOverviewCache overviewCache) : ControllerBase
{
    [HttpGet("overview")]
    public async Task<IActionResult> Overview([FromQuery] Guid? familyId, CancellationToken cancellationToken)
    {
        var resolved = await families.ResolveAsync(familyId, cancellationToken);
        if (resolved is null) return NotFound(new { error = "Family space no longer exists." });

        var overview = await overviewCache.GetOrCreateAsync(
            resolved.Value,
            token => BuildInitializedOverviewAsync(resolved.Value, token),
            cancellationToken);
        return Ok(overview);
    }

    private async Task<OverviewResponse> BuildInitializedOverviewAsync(Guid familyId, CancellationToken cancellationToken)
    {
        var family = await db.FamilySpaces
            .AsNoTracking()
            .FirstAsync(f => f.Id == familyId, cancellationToken);
        var mediaGroups = await db.MediaAssets
            .AsNoTracking()
            .Where(a => a.FamilyId == familyId)
            .GroupBy(a => a.MediaType)
            .Select(g => new OverviewMediaGroup(g.Key, g.Count(), g.Sum(a => a.Size)))
            .ToListAsync(cancellationToken);
        var latestHealth = await db.StorageHealthSnapshots
            .AsNoTracking()
            .Where(s => s.FamilyId == familyId)
            .OrderByDescending(s => s.CheckedAt)
            .FirstOrDefaultAsync(cancellationToken);
        var latestBackup = await db.BackupJobs
            .AsNoTracking()
            .Where(j => j.FamilyId == familyId)
            .OrderByDescending(j => j.StartedAt)
            .FirstOrDefaultAsync(cancellationToken);

        var (Count, Bytes) = CountArea(familyId, StorageArea.Files);
        var photos = CountArea(familyId, StorageArea.Photos);
        var backups = CountArea(familyId, StorageArea.Backups);

        return new OverviewResponse(
            Initialized: true,
            Family: new OverviewFamily(family.Id, family.Name, family.OwnerDisplayName, family.CreatedAt),
            Modules: new OverviewModules(
                Files: new OverviewAreaModule(Count, Bytes, "/dav/files/"),
                Photos: new OverviewAreaModule(photos.Count, photos.Bytes, "/dav/photos/"),
                Backups: new OverviewBackupModule(
                    backups.Count,
                    backups.Bytes,
                    await db.BackupTargets.AsNoTracking().CountAsync(t => t.FamilyId == familyId, cancellationToken),
                    latestBackup is null ? null : new OverviewLatestBackupJob(latestBackup.Id, latestBackup.State, latestBackup.StartedAt)),
                Vault: new OverviewVaultModule(
                    await db.VaultItems.AsNoTracking().CountAsync(v => v.FamilyId == familyId, cancellationToken),
                    Encrypted: true),
                Media: mediaGroups,
                Members: new OverviewMembersModule(
                    await db.FamilyMembers.AsNoTracking().CountAsync(m => m.FamilyId == familyId, cancellationToken),
                    "/api/family/permissions"),
                Devices: new OverviewDevicesModule(
                    await db.Devices.AsNoTracking().CountAsync(d => d.FamilyId == familyId, cancellationToken),
                    await db.SyncStates.AsNoTracking().CountAsync(s => s.FamilyId == familyId, cancellationToken)),
                RemoteAccess: new OverviewRemoteAccessModule(
                    await db.WireGuardPeers.AsNoTracking().CountAsync(p => p.FamilyId == familyId, cancellationToken),
                    "/api/remote/wireguard/peers"),
                Smb: new OverviewSmbModule(
                    await db.SmbShares.AsNoTracking().CountAsync(s => s.FamilyId == familyId && s.Enabled, cancellationToken),
                    await db.SmbCredentials.AsNoTracking().CountAsync(c => c.FamilyId == familyId && c.RevokedAt == null, cancellationToken),
                    "/api/smb/shares"),
                Runtime: new OverviewRuntimeModule(
                    await db.ManagedApps.AsNoTracking().CountAsync(a => a.FamilyId == familyId && a.DesiredState != "deleted", cancellationToken),
                    await db.ManagedContainers.AsNoTracking().CountAsync(c => c.FamilyId == familyId && c.DeletedAt == null, cancellationToken),
                    "/api/apps/catalog")),
            Security: new OverviewSecurity(
                LocalStorage: true,
                EndToEndEncryption: false,
                OneClickExternalBackup: false),
            Storage: latestHealth is null
                ? new OverviewStorage("not-checked", null)
                : new OverviewStorage(latestHealth.Status, latestHealth.CheckedAt),
            Ota: new OverviewOta(
                OtaRuntime.Version(),
                OtaRuntime.Channel(),
                OtaRuntime.UpdateState(),
                "/api/ota/status"));
    }

    private (long Count, long Bytes) CountArea(Guid familyId, StorageArea area)
    {
        var root = storage.GetAreaRoot(familyId, area);
        if (!Directory.Exists(root)) return (0, 0);

        long count = 0;
        long bytes = 0;
        foreach (var info in storage.EnumerateFiles(familyId, area))
        {
            count++;
            bytes += info.Length;
        }

        return (count, bytes);
    }

}
