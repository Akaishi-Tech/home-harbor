using HomeHarbor.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace HomeHarbor.Tests;

[TestClass]
public sealed class DatabaseModelTests
{
    [TestMethod]
    public void Migration_Metadata_Includes_Generated_Migrations()
    {
        using var db = CreateDbContext();

        CollectionAssert.AreEqual(
            new[]
            {
                "20260704092632_InitialPostgresSchema",
                "20260704095947_AddOverviewStatisticsIndexes",
                "20260711101552_AddFamilyRecoveryCode",
                "20260711103029_RemovePersistedPrivateKeys",
                "20260711103624_FixActiveEntityUniqueness"
            },
            db.Database.GetMigrations().ToArray());
    }

    [TestMethod]
    public void Model_Metadata_Includes_Overview_Query_Indexes()
    {
        using var db = CreateDbContext();

        AssertHasIndex<BackupJobEntity>(db, nameof(BackupJobEntity.FamilyId), nameof(BackupJobEntity.StartedAt));
        AssertHasIndex<BackupTargetEntity>(db, nameof(BackupTargetEntity.FamilyId), nameof(BackupTargetEntity.CreatedAt));
        AssertHasIndex<DeviceEntity>(db, nameof(DeviceEntity.FamilyId), nameof(DeviceEntity.LastSeenAt), nameof(DeviceEntity.CreatedAt));
        AssertHasIndex<FamilyMemberEntity>(db, nameof(FamilyMemberEntity.FamilyId), nameof(FamilyMemberEntity.CreatedAt));
        AssertHasIndex<ManagedAppEntity>(db, nameof(ManagedAppEntity.FamilyId), nameof(ManagedAppEntity.DesiredState));
        AssertHasIndex<ManagedAppEntity>(db, nameof(ManagedAppEntity.FamilyId), nameof(ManagedAppEntity.CreatedAt));
        AssertHasIndex<ManagedAppEntity>(db, nameof(ManagedAppEntity.FamilyId), nameof(ManagedAppEntity.UpdatedAt));
        AssertHasIndex<ManagedContainerEntity>(db, nameof(ManagedContainerEntity.FamilyId), nameof(ManagedContainerEntity.DeletedAt));
        AssertHasIndex<ManagedContainerEntity>(db, nameof(ManagedContainerEntity.FamilyId), nameof(ManagedContainerEntity.CreatedAt));
        AssertHasIndex<ManagedContainerEntity>(db, nameof(ManagedContainerEntity.FamilyId), nameof(ManagedContainerEntity.UpdatedAt));
        AssertHasIndex<MediaAssetEntity>(db, nameof(MediaAssetEntity.FamilyId), nameof(MediaAssetEntity.MediaType));
        AssertHasIndex<MediaAssetEntity>(db, nameof(MediaAssetEntity.FamilyId), nameof(MediaAssetEntity.LastModifiedUtc));
        AssertHasIndex<RecoveryDrillEntity>(db, nameof(RecoveryDrillEntity.FamilyId), nameof(RecoveryDrillEntity.StartedAt));
        AssertHasIndex<SmbCredentialEntity>(db, nameof(SmbCredentialEntity.FamilyId), nameof(SmbCredentialEntity.RevokedAt));
        AssertHasIndex<SmbCredentialEntity>(db, nameof(SmbCredentialEntity.ShareId), nameof(SmbCredentialEntity.RevokedAt));
        AssertHasIndex<SmbShareEntity>(db, nameof(SmbShareEntity.FamilyId), nameof(SmbShareEntity.Enabled));
        AssertHasIndex<StorageHealthEntity>(db, nameof(StorageHealthEntity.FamilyId), nameof(StorageHealthEntity.CheckedAt));
        AssertHasIndex<SyncStateEntity>(db, nameof(SyncStateEntity.FamilyId), nameof(SyncStateEntity.UpdatedAt));
        AssertHasIndex<WebDavTokenEntity>(db, nameof(WebDavTokenEntity.FamilyId), nameof(WebDavTokenEntity.CreatedAt));
        AssertHasIndex<WireGuardPeerEntity>(db, nameof(WireGuardPeerEntity.FamilyId), nameof(WireGuardPeerEntity.CreatedAt));
        Assert.IsNull(db.Model.FindEntityType(typeof(WireGuardPeerEntity))?.FindProperty("PrivateKey"));
        Assert.IsNull(db.Model.FindEntityType(typeof(CertificateEntity))?.FindProperty("PrivateKeyPem"));
        AssertUniqueIndex<FamilyMemberEntity>(db, null, nameof(FamilyMemberEntity.FamilyId), nameof(FamilyMemberEntity.DisplayName));
        AssertUniqueIndex<ManagedContainerEntity>(db, "\"DeletedAt\" IS NULL", nameof(ManagedContainerEntity.FamilyId), nameof(ManagedContainerEntity.Name));
        AssertUniqueIndex<SmbCredentialEntity>(db, "\"RevokedAt\" IS NULL", nameof(SmbCredentialEntity.Username));
        AssertUniqueIndex<SmbCredentialEntity>(db, "\"RevokedAt\" IS NULL", nameof(SmbCredentialEntity.UnixUser));
    }

    private static HomeHarborDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<HomeHarborDbContext>()
            .UseNpgsql("Host=localhost;Database=homeharbor_model_tests;Username=homeharbor;Password=unused")
            .Options;
        return new HomeHarborDbContext(options);
    }

    private static void AssertHasIndex<TEntity>(HomeHarborDbContext db, params string[] propertyNames)
    {
        var entityType = db.Model.FindEntityType(typeof(TEntity));
        Assert.IsNotNull(entityType, $"Entity type {typeof(TEntity).Name} is not registered.");

        var indexExists = entityType.GetIndexes().Any(index =>
            index.Properties.Select(property => property.Name).SequenceEqual(propertyNames));
        Assert.IsTrue(
            indexExists,
            $"{typeof(TEntity).Name} is missing index ({string.Join(", ", propertyNames)}).");
    }

    private static void AssertUniqueIndex<TEntity>(HomeHarborDbContext db, string? filter, params string[] propertyNames)
    {
        var entityType = db.Model.FindEntityType(typeof(TEntity));
        Assert.IsNotNull(entityType);
        var index = entityType.GetIndexes().Single(candidate =>
            candidate.Properties.Select(property => property.Name).SequenceEqual(propertyNames));
        Assert.IsTrue(index.IsUnique);
        Assert.AreEqual(filter, index.GetFilter());
    }
}
