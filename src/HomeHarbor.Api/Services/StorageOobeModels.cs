namespace HomeHarbor.Api.Services;

public sealed record StorageInventory(
    IReadOnlyList<StorageDevice> Devices,
    IReadOnlyList<StorageTarget> Targets,
    IReadOnlyList<StorageMount> Mounts,
    IReadOnlyList<string> ProtectedDevices,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<StorageFileSystemCapability> FileSystems);

public sealed record StorageFileSystemCapability(
    string FileSystem,
    bool Available,
    string? UnavailableReason,
    IReadOnlyList<string> RaidModes,
    bool CanPrepareOnline);

public sealed record StorageDevice(
    string? Name,
    string? Path,
    long SizeBytes,
    string? Type,
    string? Model,
    string? Serial,
    string? Transport,
    bool IsRotational,
    bool IsRemovable,
    IReadOnlyList<string> Mountpoints,
    string? FileSystem,
    string? Label,
    string? Uuid,
    string? ParentKernelName,
    bool IsSystem,
    bool IsProtected,
    SmartHealth? Smart,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<StorageDevice> Children,
    string? StableId = null);

public sealed record StorageMount(string? Target, string? Source, string? FileSystem, string? Options);

public sealed record SmartHealth(bool? Passed, int? ExitStatus, string Summary);

public sealed record StorageTarget(
    string Path,
    string Kind,
    long SizeBytes,
    string? Model,
    string? Serial,
    string? Transport,
    bool Eligible,
    IReadOnlyList<string> EligibilityReasons,
    string? StableId = null);

public sealed record StorageUseProfile(
    int FamilyMembers,
    int PhoneCount,
    int ComputerCount,
    string? PhotoVideoIntensity,
    int MediaLibraryTb,
    int Apps,
    string? BackupTargetPreference,
    string? RedundancyPreference);

public sealed record StorageRecommendation(
    string RecommendedLayout,
    IReadOnlyList<string?> SelectedDevices,
    IReadOnlyList<string?> BackupTargetDevices,
    string DataProfile,
    string MetadataProfile,
    long EstimatedOneYearBytes,
    long EstimatedThreeYearBytes,
    long UsableBytes,
    string FaultTolerance,
    IReadOnlyList<string> Warnings);

public sealed record StoragePlanRequest(
    IReadOnlyList<StoragePlanTargetRequest>? Targets,
    IReadOnlyList<string>? SelectedDevices,
    StorageUseProfile? Profile,
    string? RedundancyPreference,
    string? FileSystem,
    string? RaidMode,
    string? DataProfile,
    string? MetadataProfile,
    string? UnlockMode,
    bool AllowRemovable,
    string? PairingCode = null);

public sealed record StoragePlanTargetRequest(string Path, string? Kind);

public sealed record StoragePlan(
    string PlanId,
    string Layout,
    IReadOnlyList<StoragePlanDevice> Devices,
    string FileSystem,
    string RaidMode,
    string RaidBackend,
    string UnlockMode,
    string DataProfile,
    string MetadataProfile,
    long UsableBytes,
    IReadOnlyList<string> Operations,
    IReadOnlyList<string> DestructiveDevices,
    IReadOnlyList<StorageMountChange> MountChanges,
    bool RequiresReboot,
    bool RequiresBootloaderUnlock,
    string ConfirmPhrase,
    DateTimeOffset CreatedAt,
    IReadOnlyList<string> Warnings);

public sealed record StoragePlanDevice(
    string Path,
    string Kind,
    long SizeBytes,
    string? Model,
    string? Serial,
    string? Transport,
    string? StableId = null);

public sealed record StorageMountChange(string Target, string FileSystem, string Options);

public sealed record StorageApplyRequest(
    string PlanId,
    string Confirmation,
    string? RecoveryPassphrase,
    string? PairingCode = null);

public sealed record StorageApplyStatus(
    StorageApplyState State,
    int Progress,
    string Message,
    string? Error,
    string? PlanId,
    DateTimeOffset UpdatedAt);

public enum StorageApplyState
{
    Idle,
    PendingReboot,
    Running,
    Succeeded,
    Failed
}
