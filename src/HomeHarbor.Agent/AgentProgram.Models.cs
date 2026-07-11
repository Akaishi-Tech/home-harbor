internal static partial class AgentProgram
{
    private sealed record BootAttemptOptions(
        string StateDir,
        string Esp,
        int WindowSeconds,
        int Threshold,
        long Now,
        bool DryRun);

    private sealed record BootSuccessOptions(
        string StateDir,
        string OtaStateDir,
        string Esp,
        string BootEnv,
        string RunDir,
        int TimeoutSeconds,
        string HealthUrl,
        string ApiUrl,
        string? ApiSocket,
        bool DryRun);

    private sealed record OtaCommitOptions(
        string StateDir,
        string Esp,
        string BootEnv,
        string RunDir);

    private sealed record AppliedStoragePlan(
        string PlanId,
        string UnlockMode,
        string FileSystem,
        string RaidMode,
        string? RaidBackend,
        string DataProfile,
        string MetadataProfile,
        string FileSystemUuid,
        string? MdadmName,
        string? MdadmUuid,
        IReadOnlyList<AppliedStorageDevice> Devices,
        DateTimeOffset UpdatedAt);

    private sealed record AppliedStorageDevice(
        string Path,
        string LuksUuid,
        string MapperName);

    internal sealed record PendingStorageTarget(
        string Path,
        string Kind,
        long SizeBytes,
        string? Model,
        string? Serial,
        string? Transport,
        string? StableId,
        string? ResolvedPath = null);

    private sealed record DesiredSmbShare(
        Guid Id,
        Guid FamilyId,
        string Name,
        string ShareName,
        string Path,
        bool ReadOnly);

    private sealed record DesiredSmbCredential(
        Guid Id,
        Guid FamilyId,
        Guid ShareId,
        string UnixUser,
        bool ReadOnly);

    private sealed record CreatedDataStorage(string FileSystemUuid, string? MdadmName, string? MdadmUuid);

    private sealed record CreatedMdadmArray(string Path, string Name, string Uuid);

    private sealed record SystemAppApplyState(
        string Version,
        string RuntimeState,
        bool RequiresReboot,
        string Error);

}
