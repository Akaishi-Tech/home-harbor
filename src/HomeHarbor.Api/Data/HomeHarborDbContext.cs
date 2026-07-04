using Microsoft.EntityFrameworkCore;

namespace HomeHarbor.Api.Data;

public sealed class HomeHarborDbContext(DbContextOptions<HomeHarborDbContext> options) : DbContext(options)
{
    public DbSet<FamilySpaceEntity> FamilySpaces => Set<FamilySpaceEntity>();
    public DbSet<DeviceEntity> Devices => Set<DeviceEntity>();
    public DbSet<FamilyMemberEntity> FamilyMembers => Set<FamilyMemberEntity>();
    public DbSet<MemberSessionEntity> MemberSessions => Set<MemberSessionEntity>();
    public DbSet<WebDavTokenEntity> WebDavTokens => Set<WebDavTokenEntity>();
    public DbSet<BackupTargetEntity> BackupTargets => Set<BackupTargetEntity>();
    public DbSet<BackupJobEntity> BackupJobs => Set<BackupJobEntity>();
    public DbSet<WireGuardPeerEntity> WireGuardPeers => Set<WireGuardPeerEntity>();
    public DbSet<VaultItemEntity> VaultItems => Set<VaultItemEntity>();
    public DbSet<MediaAssetEntity> MediaAssets => Set<MediaAssetEntity>();
    public DbSet<SyncStateEntity> SyncStates => Set<SyncStateEntity>();
    public DbSet<ManagedAppEntity> ManagedApps => Set<ManagedAppEntity>();
    public DbSet<ManagedContainerEntity> ManagedContainers => Set<ManagedContainerEntity>();
    public DbSet<SmbShareEntity> SmbShares => Set<SmbShareEntity>();
    public DbSet<SmbCredentialEntity> SmbCredentials => Set<SmbCredentialEntity>();
    public DbSet<CertificateEntity> Certificates => Set<CertificateEntity>();
    public DbSet<ReverseProxyRouteEntity> ReverseProxyRoutes => Set<ReverseProxyRouteEntity>();
    public DbSet<RecoveryDrillEntity> RecoveryDrills => Set<RecoveryDrillEntity>();
    public DbSet<StorageHealthEntity> StorageHealthSnapshots => Set<StorageHealthEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        _ = modelBuilder.Entity<FamilySpaceEntity>(entity =>
        {
            _ = entity.HasKey(e => e.Id);
            _ = entity.HasIndex(e => e.CreatedAt);
            _ = entity.Property(e => e.Name).HasMaxLength(96);
            _ = entity.Property(e => e.OwnerDisplayName).HasMaxLength(96);
        });

        _ = modelBuilder.Entity<DeviceEntity>(entity =>
        {
            _ = entity.HasKey(e => e.Id);
            _ = entity.HasIndex(e => new { e.FamilyId, e.DisplayName });
            _ = entity.HasIndex(e => new { e.FamilyId, e.LastSeenAt, e.CreatedAt });
            _ = entity.Property(e => e.DisplayName).HasMaxLength(96);
            _ = entity.Property(e => e.Kind).HasMaxLength(32);
        });

        _ = modelBuilder.Entity<FamilyMemberEntity>(entity =>
        {
            _ = entity.HasKey(e => e.Id);
            _ = entity.HasIndex(e => new { e.FamilyId, e.DisplayName });
            _ = entity.HasIndex(e => new { e.FamilyId, e.CreatedAt });
            _ = entity.Property(e => e.DisplayName).HasMaxLength(96);
            _ = entity.Property(e => e.Role).HasMaxLength(32);
            _ = entity.Property(e => e.PasswordHash).HasMaxLength(256);
        });

        _ = modelBuilder.Entity<MemberSessionEntity>(entity =>
        {
            _ = entity.HasKey(e => e.Id);
            _ = entity.HasIndex(e => e.TokenHash).IsUnique();
            _ = entity.Property(e => e.TokenHash).HasMaxLength(256);
        });

        _ = modelBuilder.Entity<WebDavTokenEntity>(entity =>
        {
            _ = entity.HasKey(e => e.Id);
            _ = entity.HasIndex(e => e.Username).IsUnique();
            _ = entity.HasIndex(e => new { e.FamilyId, e.CreatedAt });
            _ = entity.Property(e => e.Username).HasMaxLength(64);
            _ = entity.Property(e => e.TokenHash).HasMaxLength(256);
            _ = entity.Property(e => e.Scope).HasConversion<string>().HasMaxLength(16);
            _ = entity.Property(e => e.Description).HasMaxLength(160);
        });

        _ = modelBuilder.Entity<BackupTargetEntity>(entity =>
        {
            _ = entity.HasKey(e => e.Id);
            _ = entity.HasIndex(e => new { e.FamilyId, e.CreatedAt });
            _ = entity.Property(e => e.Name).HasMaxLength(96);
            _ = entity.Property(e => e.RepositoryUri).HasMaxLength(1024);
        });

        _ = modelBuilder.Entity<BackupJobEntity>(entity =>
        {
            _ = entity.HasKey(e => e.Id);
            _ = entity.HasIndex(e => new { e.FamilyId, e.StartedAt });
            _ = entity.Property(e => e.State).HasMaxLength(32);
            _ = entity.Property(e => e.Command).HasMaxLength(2048);
            _ = entity.Property(e => e.Result).HasMaxLength(4096);
        });

        _ = modelBuilder.Entity<WireGuardPeerEntity>(entity =>
        {
            _ = entity.HasKey(e => e.Id);
            _ = entity.HasIndex(e => new { e.FamilyId, e.CreatedAt });
            _ = entity.Property(e => e.Name).HasMaxLength(96);
            _ = entity.Property(e => e.PublicKey).HasMaxLength(128);
            _ = entity.Property(e => e.PrivateKey).HasMaxLength(128);
            _ = entity.Property(e => e.Address).HasMaxLength(64);
        });

        _ = modelBuilder.Entity<VaultItemEntity>(entity =>
        {
            _ = entity.HasKey(e => e.Id);
            _ = entity.HasIndex(e => new { e.FamilyId, e.Name });
            _ = entity.Property(e => e.Name).HasMaxLength(160);
            _ = entity.Property(e => e.Nonce).HasMaxLength(128);
            _ = entity.Property(e => e.KeyHint).HasMaxLength(128);
        });

        _ = modelBuilder.Entity<MediaAssetEntity>(entity =>
        {
            _ = entity.HasKey(e => e.Id);
            _ = entity.HasIndex(e => new { e.FamilyId, e.Area, e.RelativePath }).IsUnique();
            _ = entity.HasIndex(e => new { e.FamilyId, e.MediaType });
            _ = entity.HasIndex(e => new { e.FamilyId, e.LastModifiedUtc });
            _ = entity.Property(e => e.Area).HasMaxLength(32);
            _ = entity.Property(e => e.RelativePath).HasMaxLength(2048);
            _ = entity.Property(e => e.MediaType).HasMaxLength(32);
        });

        _ = modelBuilder.Entity<SyncStateEntity>(entity =>
        {
            _ = entity.HasKey(e => e.Id);
            _ = entity.HasIndex(e => new { e.FamilyId, e.DeviceId, e.Scope }).IsUnique();
            _ = entity.HasIndex(e => new { e.FamilyId, e.UpdatedAt });
            _ = entity.Property(e => e.Scope).HasMaxLength(64);
            _ = entity.Property(e => e.Cursor).HasMaxLength(2048);
        });

        _ = modelBuilder.Entity<ManagedAppEntity>(entity =>
        {
            _ = entity.HasKey(e => e.Id);
            _ = entity.HasIndex(e => new { e.FamilyId, e.AppKey }).IsUnique();
            _ = entity.HasIndex(e => new { e.FamilyId, e.DesiredState });
            _ = entity.HasIndex(e => new { e.FamilyId, e.CreatedAt });
            _ = entity.HasIndex(e => new { e.FamilyId, e.UpdatedAt });
            _ = entity.Property(e => e.AppKey).HasMaxLength(96);
            _ = entity.Property(e => e.Kind).HasMaxLength(32);
            _ = entity.Property(e => e.DisplayName).HasMaxLength(96);
            _ = entity.Property(e => e.Image).HasMaxLength(256);
            _ = entity.Property(e => e.State).HasMaxLength(32);
            _ = entity.Property(e => e.DesiredState).HasMaxLength(32);
            _ = entity.Property(e => e.RuntimeState).HasMaxLength(32);
            _ = entity.Property(e => e.InstalledVersion).HasMaxLength(64);
            _ = entity.Property(e => e.ActiveVersion).HasMaxLength(64);
            _ = entity.Property(e => e.LastError).HasMaxLength(4096);
        });

        _ = modelBuilder.Entity<ManagedContainerEntity>(entity =>
        {
            _ = entity.HasKey(e => e.Id);
            _ = entity.HasIndex(e => new { e.FamilyId, e.Name }).IsUnique();
            _ = entity.HasIndex(e => new { e.FamilyId, e.DeletedAt });
            _ = entity.HasIndex(e => new { e.FamilyId, e.CreatedAt });
            _ = entity.HasIndex(e => new { e.FamilyId, e.UpdatedAt });
            _ = entity.Property(e => e.Name).HasMaxLength(96);
            _ = entity.Property(e => e.Image).HasMaxLength(512);
            _ = entity.Property(e => e.DesiredState).HasMaxLength(32);
            _ = entity.Property(e => e.RuntimeState).HasMaxLength(32);
            _ = entity.Property(e => e.RequestedAction).HasMaxLength(32);
            _ = entity.Property(e => e.ServiceName).HasMaxLength(128);
            _ = entity.Property(e => e.LastError).HasMaxLength(4096);
        });

        _ = modelBuilder.Entity<SmbShareEntity>(entity =>
        {
            _ = entity.HasKey(e => e.Id);
            _ = entity.HasIndex(e => new { e.FamilyId, e.ShareName }).IsUnique();
            _ = entity.HasIndex(e => new { e.FamilyId, e.Enabled });
            _ = entity.Property(e => e.Name).HasMaxLength(96);
            _ = entity.Property(e => e.ShareName).HasMaxLength(64);
            _ = entity.Property(e => e.Path).HasMaxLength(1024);
            _ = entity.Property(e => e.RuntimeState).HasMaxLength(32);
            _ = entity.Property(e => e.LastError).HasMaxLength(4096);
        });

        _ = modelBuilder.Entity<SmbCredentialEntity>(entity =>
        {
            _ = entity.HasKey(e => e.Id);
            _ = entity.HasIndex(e => e.Username).IsUnique();
            _ = entity.HasIndex(e => e.UnixUser).IsUnique();
            _ = entity.HasIndex(e => new { e.FamilyId, e.RevokedAt });
            _ = entity.HasIndex(e => new { e.ShareId, e.RevokedAt });
            _ = entity.Property(e => e.DisplayName).HasMaxLength(96);
            _ = entity.Property(e => e.Username).HasMaxLength(64);
            _ = entity.Property(e => e.UnixUser).HasMaxLength(64);
            _ = entity.Property(e => e.PasswordHash).HasMaxLength(256);
            _ = entity.Property(e => e.RuntimeState).HasMaxLength(32);
            _ = entity.Property(e => e.LastError).HasMaxLength(4096);
        });

        _ = modelBuilder.Entity<CertificateEntity>(entity =>
        {
            _ = entity.HasKey(e => e.Id);
            _ = entity.HasIndex(e => new { e.FamilyId, e.Hostname }).IsUnique();
            _ = entity.Property(e => e.Hostname).HasMaxLength(253);
            _ = entity.Property(e => e.Kind).HasMaxLength(32);
        });

        _ = modelBuilder.Entity<ReverseProxyRouteEntity>(entity =>
        {
            _ = entity.HasKey(e => e.Id);
            _ = entity.HasIndex(e => new { e.FamilyId, e.Hostname }).IsUnique();
            _ = entity.Property(e => e.Hostname).HasMaxLength(253);
            _ = entity.Property(e => e.UpstreamUrl).HasMaxLength(1024);
        });

        _ = modelBuilder.Entity<RecoveryDrillEntity>(entity =>
        {
            _ = entity.HasKey(e => e.Id);
            _ = entity.HasIndex(e => new { e.FamilyId, e.StartedAt });
            _ = entity.Property(e => e.State).HasMaxLength(32);
            _ = entity.Property(e => e.Result).HasMaxLength(4096);
        });

        _ = modelBuilder.Entity<StorageHealthEntity>(entity =>
        {
            _ = entity.HasKey(e => e.Id);
            _ = entity.HasIndex(e => new { e.FamilyId, e.CheckedAt });
            _ = entity.Property(e => e.DataRoot).HasMaxLength(1024);
            _ = entity.Property(e => e.Status).HasMaxLength(32);
            _ = entity.Property(e => e.Notes).HasMaxLength(4096);
        });
    }
}
