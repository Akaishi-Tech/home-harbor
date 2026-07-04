namespace HomeHarbor.Api.Services;

public sealed record OverviewResponse(
    bool Initialized,
    OverviewFamily Family,
    OverviewModules Modules,
    OverviewSecurity Security,
    OverviewStorage Storage,
    OverviewOta Ota);

public sealed record OverviewFamily(
    Guid Id,
    string Name,
    string OwnerDisplayName,
    DateTimeOffset CreatedAt);

public sealed record OverviewModules(
    OverviewAreaModule Files,
    OverviewAreaModule Photos,
    OverviewBackupModule Backups,
    OverviewVaultModule Vault,
    IReadOnlyList<OverviewMediaGroup> Media,
    OverviewMembersModule Members,
    OverviewDevicesModule Devices,
    OverviewRemoteAccessModule RemoteAccess,
    OverviewSmbModule Smb,
    OverviewRuntimeModule Runtime);

public sealed record OverviewAreaModule(long Count, long Bytes, string WebDav);

public sealed record OverviewBackupModule(
    long LocalCount,
    long LocalBytes,
    int TargetCount,
    OverviewLatestBackupJob? LatestJob);

public sealed record OverviewLatestBackupJob(Guid Id, string State, DateTimeOffset StartedAt);

public sealed record OverviewVaultModule(int Count, bool Encrypted);

public sealed record OverviewMediaGroup(string Type, int Count, long Bytes);

public sealed record OverviewMembersModule(int Count, string Permissions);

public sealed record OverviewDevicesModule(int Count, int SyncStates);

public sealed record OverviewRemoteAccessModule(int Peers, string Endpoint);

public sealed record OverviewSmbModule(int Shares, int Credentials, string Endpoint);

public sealed record OverviewRuntimeModule(int Apps, int Containers, string Catalog);

public sealed record OverviewSecurity(
    bool LocalStorage,
    bool EndToEndEncryption,
    bool OneClickExternalBackup);

public sealed record OverviewStorage(string Status, DateTimeOffset? CheckedAt);

public sealed record OverviewOta(
    string Version,
    string Channel,
    string UpdateState,
    string Status);
