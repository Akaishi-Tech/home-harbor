using System.Collections.Concurrent;
using HomeHarbor.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace HomeHarbor.Api.Data;

public sealed class OverviewCacheInvalidationInterceptor(IOverviewCacheInvalidator cache)
    : SaveChangesInterceptor
{
    private readonly ConcurrentDictionary<Guid, IReadOnlySet<Guid>> _pendingFamilyIds = new();

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        CaptureFamilyIds(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        CaptureFamilyIds(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    public override async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        if (eventData.Context is not null &&
            _pendingFamilyIds.TryRemove(eventData.Context.ContextId.InstanceId, out var familyIds))
        {
            foreach (var familyId in familyIds)
            {
                await cache.InvalidateFamilyAsync(familyId, cancellationToken);
            }
        }

        return await base.SavedChangesAsync(eventData, result, cancellationToken);
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData)
    {
        if (eventData.Context is not null)
        {
            _ = _pendingFamilyIds.TryRemove(eventData.Context.ContextId.InstanceId, out _);
        }

        base.SaveChangesFailed(eventData);
    }

    public override Task SaveChangesFailedAsync(
        DbContextErrorEventData eventData,
        CancellationToken cancellationToken = default)
    {
        if (eventData.Context is not null)
        {
            _ = _pendingFamilyIds.TryRemove(eventData.Context.ContextId.InstanceId, out _);
        }

        return base.SaveChangesFailedAsync(eventData, cancellationToken);
    }

    private void CaptureFamilyIds(DbContext? context)
    {
        if (context is null) return;

        var familyIds = CollectFamilyIds(context.ChangeTracker);
        if (familyIds.Count > 0)
        {
            _pendingFamilyIds[context.ContextId.InstanceId] = familyIds;
        }
    }

    private static IReadOnlySet<Guid> CollectFamilyIds(ChangeTracker changeTracker)
    {
        var familyIds = new HashSet<Guid>();
        foreach (var entry in changeTracker.Entries())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted))
                continue;

            if (TryGetFamilyId(entry.Entity, out var familyId))
            {
                _ = familyIds.Add(familyId);
            }
        }

        return familyIds;
    }

    private static bool TryGetFamilyId(object entity, out Guid familyId)
    {
        familyId = entity switch
        {
            FamilySpaceEntity family => family.Id,
            DeviceEntity item => item.FamilyId,
            FamilyMemberEntity item => item.FamilyId,
            BackupTargetEntity item => item.FamilyId,
            BackupJobEntity item => item.FamilyId,
            WireGuardPeerEntity item => item.FamilyId,
            VaultItemEntity item => item.FamilyId,
            MediaAssetEntity item => item.FamilyId,
            SyncStateEntity item => item.FamilyId,
            ManagedAppEntity item => item.FamilyId,
            ManagedContainerEntity item => item.FamilyId,
            SmbShareEntity item => item.FamilyId,
            SmbCredentialEntity item => item.FamilyId,
            StorageHealthEntity item => item.FamilyId,
            _ => Guid.Empty
        };
        return familyId != Guid.Empty;
    }
}
