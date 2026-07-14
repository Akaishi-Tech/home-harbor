using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace HomeHarbor.Tooling;

public sealed record SelinuxPolicyEpoch(
    int SchemaVersion,
    string StoreSha256,
    string LabelContractSha256,
    string Generation);

public static partial class SelinuxRelabelCoordinator
{
    public const string ContextsPath = "/etc/selinux/refpolicy-arch/contexts/files";
    public const string PolicyPath = "/etc/selinux/refpolicy-arch/policy";
    public const string EpochPath = "/var/lib/selinux/.homeharbor-relabel-epoch";
    public const string PersistentMarkerPath = "/var/lib/selinux/.homeharbor-relabel-var";
    public const string PersistentRootPath = "/var";
    public const string DataRootPath = "/homeharbor-data";
    public const string DataMarkerPath = "/homeharbor-data/.homeharbor-relabel";
    public const string LockPath = "/var/lib/selinux/.homeharbor-relabel.lock";

    private const int EpochSchemaVersion = 1;
    private const string PersistentScope = "persistent";
    private const string DataScope = "data";
    private const int MarkerMaximumBytes = 4096;

    private static readonly string[] DataManagedPaths =
    [
        DataRootPath,
        DataRootPath + "/apps",
        DataRootPath + "/families",
        DataRootPath + "/system-apps",
        DataRootPath + "/system-apps/active",
        DataRootPath + "/system-apps/staged",
        DataRootPath + "/system-apps/versions",
        DataRootPath + "/postgresql",
        DataRootPath + "/postgresql/data"
    ];

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> ProcessLocks =
        new(StringComparer.Ordinal);

    internal sealed record Paths(
        string ImmutableSeed,
        string RuntimeStore,
        string Contexts,
        string Policy,
        string Epoch,
        string PersistentMarker,
        string PersistentRoot,
        string DataMarker,
        string DataRoot,
        string Lock,
        string MountInfo = "/proc/self/mountinfo");

    internal sealed record ManagedPath(string Path, bool Required);

    internal static IReadOnlyList<ManagedPath> SystemManagedPaths { get; } =
        SelinuxRuntimeReadiness.SystemDirectories
            .Select(requirement => new ManagedPath(requirement.Path, Required: true))
            .Concat(
            [
                new ManagedPath("/var/lib/homeharbor/storage", Required: false),
                new ManagedPath("/var/lib/homeharbor/ota/channel", Required: false),
                new ManagedPath("/var/lib/homeharbor/ota/kernel-channel", Required: false),
                new ManagedPath("/var/lib/systemd/linger", Required: false),
                new ManagedPath("/var/lib/systemd/linger/homeharbor-containers", Required: false)
            ])
            .ToArray();

    internal static IReadOnlyList<ManagedPath> RecoveryManagedPaths { get; } =
        SelinuxRuntimeReadiness.RecoveryDirectories
            .Select(requirement => new ManagedPath(requirement.Path, Required: true))
            .ToArray();

    internal static Paths DefaultPaths { get; } = new(
        SelinuxPolicyStoreSynchronizer.ImmutableSeedPath,
        SelinuxPolicyStoreSynchronizer.RuntimeStorePath,
        ContextsPath,
        PolicyPath,
        EpochPath,
        PersistentMarkerPath,
        PersistentRootPath,
        DataMarkerPath,
        DataRootPath,
        LockPath);

    public static Task<bool> RelabelPersistentDefaultAsync(
        ICommandRunner runner,
        CancellationToken cancellationToken = default)
    {
        RequireDefaultEnvironment();
        return RelabelPersistentAsync(runner, DefaultPaths, cancellationToken);
    }

    public static Task<bool> RelabelDataDefaultAsync(
        ICommandRunner runner,
        CancellationToken cancellationToken = default)
    {
        RequireDefaultEnvironment();
        return RelabelDataAsync(runner, DefaultPaths, requireMountPoint: true, cancellationToken);
    }

    public static Task RelabelManagedPathsDefaultAsync(
        ICommandRunner runner,
        CancellationToken cancellationToken = default)
    {
        RequireDefaultEnvironment();
        return RelabelManagedPathsAsync(runner, DefaultPaths, DefaultManagedPaths(), cancellationToken);
    }

    public static void RequirePersistentCurrentDefault()
    {
        RequireDefaultEnvironment();
        using var relabelLock = AcquireLockAsync(DefaultPaths.Lock, CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        var epoch = RequireCurrentEpochUnlocked(DefaultPaths);
        RequireCurrentMarker(DefaultPaths.PersistentMarker, PersistentScope, epoch);
    }

    internal static SelinuxPolicyStoreSynchronizationResult SynchronizeDefaultDetailed()
        => SynchronizeDetailed(DefaultPaths);

    internal static SelinuxPolicyStoreSynchronizationResult SynchronizeDetailed(
        Paths paths,
        Action<SelinuxPolicyEpoch>? afterEpochPersisted = null)
    {
        using var relabelLock = AcquireLockAsync(paths.Lock, CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        ValidateCoordinatorPaths(paths);
        var storeSha256 = SelinuxPolicyStoreSynchronizer.RequireValidSeed(paths.ImmutableSeed);
        var labelContractSha256 = ComputeLabelContractDigest(
            storeSha256,
            paths.Contexts,
            paths.Policy);
        var epoch = TryReadEpoch(paths.Epoch, tolerateMalformed: true);
        var epochPrepared = false;

        if (epoch is null ||
            !string.Equals(epoch.StoreSha256, storeSha256, StringComparison.Ordinal) ||
            !string.Equals(epoch.LabelContractSha256, labelContractSha256, StringComparison.Ordinal))
        {
            epoch = CreateEpoch(storeSha256, labelContractSha256);
            WriteEpoch(paths.Epoch, epoch);
            epochPrepared = true;
            afterEpochPersisted?.Invoke(epoch);
        }

        var synchronized = SelinuxPolicyStoreSynchronizer.SynchronizeDetailed(
            paths.ImmutableSeed,
            paths.RuntimeStore,
            replacementStoreSha256 =>
            {
                if (!string.Equals(replacementStoreSha256, storeSha256, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "SELinux policy store digest changed during synchronization");
                }

                if (!epochPrepared)
                {
                    epoch = CreateEpoch(storeSha256, labelContractSha256);
                    WriteEpoch(paths.Epoch, epoch);
                    epochPrepared = true;
                    afterEpochPersisted?.Invoke(epoch);
                }
            });

        if (!string.Equals(synchronized.StoreSha256, storeSha256, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("synchronized SELinux policy store has an unexpected digest");
        }

        return new SelinuxPolicyStoreSynchronizationResult(epoch, synchronized.StoreReplaced);
    }

    internal static async Task<bool> RelabelPersistentAsync(
        ICommandRunner runner,
        Paths paths,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runner);
        await using var relabelLock = await AcquireLockAsync(paths.Lock, cancellationToken);
        var epoch = RequireCurrentEpochUnlocked(paths);
        return await RelabelScopeUnlockedAsync(
            runner,
            paths.PersistentRoot,
            paths.PersistentMarker,
            PersistentScope,
            epoch,
            recursive: true,
            cancellationToken);
    }

    internal static async Task<bool> RelabelDataAsync(
        ICommandRunner runner,
        Paths paths,
        bool requireMountPoint,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runner);
        await using var relabelLock = await AcquireLockAsync(paths.Lock, cancellationToken);
        var epoch = RequireCurrentEpochUnlocked(paths);
        RequireRealDirectory(paths.DataRoot, "HomeHarbor data root");
        if (requireMountPoint && !IsMountPoint(paths.MountInfo, paths.DataRoot))
        {
            throw new InvalidOperationException(
                "HomeHarbor data relabel requires a mounted data filesystem: " + paths.DataRoot);
        }

        var markerCurrent = MarkerMatches(TryReadMarker(paths.DataMarker), DataScope, epoch);
        if (!markerCurrent)
        {
            _ = (await runner.RunAsync(
                    "restorecon",
                    ["-Rx", Path.GetFullPath(paths.DataRoot)],
                    cancellationToken: cancellationToken))
                .EnsureSuccess("failed to relabel data SELinux paths");
        }

        var defaultDataRoot = Path.GetFullPath(DataRootPath);
        var actualDataRoot = Path.GetFullPath(paths.DataRoot);
        var managedDataPaths = DataManagedPaths.Select(path =>
        {
            var relative = Path.GetRelativePath(defaultDataRoot, Path.GetFullPath(path));
            return relative == "." ? actualDataRoot : Path.Combine(actualDataRoot, relative);
        }).Concat(markerCurrent ? [Path.GetFullPath(paths.DataMarker)] : []);
        await RelabelExistingPathsUnlockedAsync(
            runner,
            managedDataPaths,
            skipPath: markerCurrent ? null : actualDataRoot,
            cancellationToken);

        if (!markerCurrent)
        {
            WriteMarker(paths.DataMarker, DataScope, epoch);
            await RelabelExistingPathsUnlockedAsync(
                runner,
                [paths.DataMarker],
                skipPath: null,
                cancellationToken);
        }

        return !markerCurrent;
    }

    internal static async Task RelabelManagedPathsAsync(
        ICommandRunner runner,
        Paths paths,
        IEnumerable<string> managedPaths,
        CancellationToken cancellationToken)
        => await RelabelManagedPathsAsync(
            runner,
            paths,
            managedPaths.Select(path => new ManagedPath(path, Required: true)),
            cancellationToken);

    internal static async Task RelabelManagedPathsAsync(
        ICommandRunner runner,
        Paths paths,
        IEnumerable<ManagedPath> managedPaths,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(managedPaths);
        await using var relabelLock = await AcquireLockAsync(paths.Lock, cancellationToken);
        var epoch = RequireCurrentEpochUnlocked(paths);
        RequireCurrentMarker(paths.PersistentMarker, PersistentScope, epoch);

        foreach (var managedPath in managedPaths
                     .GroupBy(path => Path.GetFullPath(path.Path), StringComparer.Ordinal)
                     .Select(group => new ManagedPath(
                         group.Key,
                         group.Any(path => path.Required)))
                     .OrderBy(path => path.Path, StringComparer.Ordinal))
        {
            if (!EntryExists(managedPath.Path))
            {
                if (managedPath.Required)
                {
                    throw new FileNotFoundException(
                        "required managed SELinux path is missing",
                        managedPath.Path);
                }

                continue;
            }

            RequireRealEntry(managedPath.Path, "managed SELinux path");
            _ = (await runner.RunAsync(
                    "restorecon",
                    [managedPath.Path],
                    cancellationToken: cancellationToken))
                .EnsureSuccess("failed to label managed SELinux path " + managedPath.Path);
        }
    }

    internal static string ComputeLabelContractDigest(
        string storeSha256,
        string contexts,
        string policy)
    {
        RequireSha256(storeSha256, "SELinux store digest");
        var contextRoot = Path.GetFullPath(contexts);
        var policyRoot = Path.GetFullPath(policy);
        RequireNonemptyRealDirectory(contextRoot, "SELinux file-context tree");
        RequireNonemptyRealDirectory(policyRoot, "SELinux compiled-policy tree");

        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendText(digest, "homeharbor-selinux-label-contract-v1\n");
        AppendText(digest, "store\0" + storeSha256 + "\n");
        AppendDirectory(digest, contextRoot, contextRoot, "contexts");
        AppendDirectory(digest, policyRoot, policyRoot, "policy");
        return Convert.ToHexStringLower(digest.GetHashAndReset());
    }

    internal static bool IsMountPoint(string mountInfo, string path)
    {
        var expected = Path.GetFullPath(path);
        foreach (var line in File.ReadLines(mountInfo))
        {
            var separator = line.IndexOf(" - ", StringComparison.Ordinal);
            var fields = (separator < 0 ? line : line[..separator])
                .Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length >= 5 &&
                string.Equals(Path.GetFullPath(UnescapeMountInfo(fields[4])), expected, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<bool> RelabelScopeUnlockedAsync(
        ICommandRunner runner,
        string root,
        string markerPath,
        string scope,
        SelinuxPolicyEpoch epoch,
        bool recursive,
        CancellationToken cancellationToken)
    {
        RequireRealDirectory(root, scope + " SELinux relabel root");
        var marker = TryReadMarker(markerPath);
        if (MarkerMatches(marker, scope, epoch))
        {
            return false;
        }

        var arguments = recursive ? new[] { "-Rx", Path.GetFullPath(root) } : [Path.GetFullPath(root)];
        _ = (await runner.RunAsync(
                "restorecon",
                arguments,
                cancellationToken: cancellationToken))
            .EnsureSuccess("failed to relabel " + scope + " SELinux paths");

        WriteMarker(markerPath, scope, epoch);
        return true;
    }

    private static SelinuxPolicyEpoch RequireCurrentEpochUnlocked(Paths paths)
    {
        ValidateCoordinatorPaths(paths);
        var storeSha256 = SelinuxPolicyStoreSynchronizer.RequireValidSeed(paths.ImmutableSeed);
        if (!SelinuxPolicyStoreSynchronizer.StoreMatches(paths.RuntimeStore, storeSha256))
        {
            throw new InvalidOperationException(
                "runtime SELinux policy store does not match the immutable image seed");
        }

        var labelContractSha256 = ComputeLabelContractDigest(
            storeSha256,
            paths.Contexts,
            paths.Policy);
        var epoch = TryReadEpoch(paths.Epoch, tolerateMalformed: false)
            ?? throw new InvalidOperationException("SELinux relabel epoch is missing: " + paths.Epoch);
        if (!string.Equals(epoch.StoreSha256, storeSha256, StringComparison.Ordinal) ||
            !string.Equals(epoch.LabelContractSha256, labelContractSha256, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "SELinux relabel epoch does not match the active policy contract");
        }

        return epoch;
    }

    private static void RequireCurrentMarker(
        string markerPath,
        string scope,
        SelinuxPolicyEpoch epoch)
    {
        if (!MarkerMatches(TryReadMarker(markerPath), scope, epoch))
        {
            throw new InvalidOperationException(
                $"{scope} SELinux relabel is incomplete for the current policy epoch");
        }
    }

    private static IReadOnlyList<ManagedPath> DefaultManagedPaths()
    {
        if (File.Exists("/usr/lib/homeharbor/api/HomeHarbor.Api"))
        {
            return SystemManagedPaths;
        }

        if (File.Exists("/usr/lib/homeharbor/recovery/HomeHarbor.Recovery"))
        {
            return RecoveryManagedPaths;
        }

        throw new InvalidOperationException(
            "could not identify the HomeHarbor system or recovery SELinux relabel profile");
    }

    private static async Task RelabelExistingPathsUnlockedAsync(
        ICommandRunner runner,
        IEnumerable<string> paths,
        string? skipPath,
        CancellationToken cancellationToken)
    {
        foreach (var path in paths
                     .Select(Path.GetFullPath)
                     .Distinct(StringComparer.Ordinal))
        {
            if (string.Equals(path, skipPath, StringComparison.Ordinal) || !EntryExists(path))
            {
                continue;
            }

            RequireRealEntry(path, "managed SELinux path");
            _ = (await runner.RunAsync(
                    "restorecon",
                    [path],
                    cancellationToken: cancellationToken))
                .EnsureSuccess("failed to label managed SELinux path " + path);
        }
    }

    private static SelinuxPolicyEpoch CreateEpoch(
        string storeSha256,
        string labelContractSha256)
        => new(
            EpochSchemaVersion,
            storeSha256,
            labelContractSha256,
            Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)));

    private static SelinuxPolicyEpoch? TryReadEpoch(string path, bool tolerateMalformed)
    {
        if (!EntryExists(path))
        {
            return null;
        }

        RequireRegularFile(path, "SELinux relabel epoch");
        try
        {
            var lines = ReadMarkerLines(path);
            if (lines.Length != 4 || lines[0] != "homeharbor-selinux-epoch-v1")
            {
                throw new InvalidDataException("SELinux relabel epoch has an invalid schema");
            }

            var storeSha256 = RequireField(lines[1], "store-sha256");
            var labelContractSha256 = RequireField(lines[2], "label-contract-sha256");
            var generation = RequireField(lines[3], "generation");
            RequireSha256(storeSha256, "SELinux relabel epoch store digest");
            RequireSha256(labelContractSha256, "SELinux relabel epoch label-contract digest");
            RequireGeneration(generation);
            return new SelinuxPolicyEpoch(
                EpochSchemaVersion,
                storeSha256,
                labelContractSha256,
                generation);
        }
        catch (InvalidDataException) when (tolerateMalformed)
        {
            return null;
        }
    }

    private static SelinuxRelabelMarker? TryReadMarker(string path)
    {
        if (!EntryExists(path))
        {
            return null;
        }

        RequireRegularFile(path, "SELinux relabel completion marker");
        try
        {
            var lines = ReadMarkerLines(path);
            if (lines.Length != 5 || lines[0] != "homeharbor-selinux-marker-v1")
            {
                return null;
            }

            var scope = RequireField(lines[1], "scope");
            var storeSha256 = RequireField(lines[2], "store-sha256");
            var labelContractSha256 = RequireField(lines[3], "label-contract-sha256");
            var generation = RequireField(lines[4], "generation");
            RequireSha256(storeSha256, "SELinux relabel marker store digest");
            RequireSha256(labelContractSha256, "SELinux relabel marker label-contract digest");
            RequireGeneration(generation);
            return new SelinuxRelabelMarker(
                EpochSchemaVersion,
                scope,
                storeSha256,
                labelContractSha256,
                generation);
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    private static bool MarkerMatches(
        SelinuxRelabelMarker? marker,
        string scope,
        SelinuxPolicyEpoch epoch)
        => marker is not null &&
           marker.SchemaVersion == epoch.SchemaVersion &&
           string.Equals(marker.Scope, scope, StringComparison.Ordinal) &&
           string.Equals(marker.StoreSha256, epoch.StoreSha256, StringComparison.Ordinal) &&
           string.Equals(marker.LabelContractSha256, epoch.LabelContractSha256, StringComparison.Ordinal) &&
           string.Equals(marker.Generation, epoch.Generation, StringComparison.Ordinal);

    private static void WriteEpoch(string path, SelinuxPolicyEpoch epoch)
        => DurableAtomicWrite(
            path,
            "homeharbor-selinux-epoch-v1\n" +
            "store-sha256=" + epoch.StoreSha256 + "\n" +
            "label-contract-sha256=" + epoch.LabelContractSha256 + "\n" +
            "generation=" + epoch.Generation + "\n");

    private static void WriteMarker(
        string path,
        string scope,
        SelinuxPolicyEpoch epoch)
        => DurableAtomicWrite(
            path,
            "homeharbor-selinux-marker-v1\n" +
            "scope=" + scope + "\n" +
            "store-sha256=" + epoch.StoreSha256 + "\n" +
            "label-contract-sha256=" + epoch.LabelContractSha256 + "\n" +
            "generation=" + epoch.Generation + "\n");

    private static void DurableAtomicWrite(string path, string contents)
    {
        var destination = Path.GetFullPath(path);
        var parent = Path.GetDirectoryName(destination)
            ?? throw new InvalidOperationException("SELinux marker has no parent directory: " + destination);
        RequireRealDirectory(parent, "SELinux marker parent");
        RefuseSymbolicLink(destination, "SELinux marker");

        var temp = destination + ".tmp." + Environment.ProcessId + "." + Guid.NewGuid().ToString("N");
        try
        {
            var options = new FileStreamOptions
            {
                Access = FileAccess.Write,
                Mode = FileMode.CreateNew,
                Share = FileShare.None,
                Options = FileOptions.WriteThrough,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
            };
            using (var stream = new FileStream(temp, options))
            {
                var bytes = Encoding.UTF8.GetBytes(contents);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            RefuseSymbolicLink(destination, "SELinux marker");
            File.Move(temp, destination, overwrite: true);
            FsyncDirectory(parent);
        }
        finally
        {
            if (File.Exists(temp) || new FileInfo(temp).LinkTarget is not null)
            {
                File.Delete(temp);
            }
        }
    }

    private static async Task<RelabelLock> AcquireLockAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        var parent = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("SELinux relabel lock has no parent directory");
        RequireRealDirectory(parent, "SELinux relabel lock parent");
        RefuseSymbolicLink(fullPath, "SELinux relabel lock");

        var processLock = ProcessLocks.GetOrAdd(fullPath, static _ => new SemaphoreSlim(1, 1));
        await processLock.WaitAsync(cancellationToken);
        SafeFileHandle? handle = null;
        try
        {
            var descriptor = NativeMethods.Open(
                fullPath,
                NativeMethods.OpenReadWrite | NativeMethods.OpenCreate |
                NativeMethods.OpenCloseOnExec | NativeMethods.OpenNoFollow,
                0x180u);
            if (descriptor < 0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(),
                    "could not open SELinux relabel lock " + fullPath);
            }

            handle = new SafeFileHandle((nint)descriptor, ownsHandle: true);
            RequireRegularSingleLinkLock(descriptor, fullPath);
            if (NativeMethods.Fchmod(descriptor, 0x180u) != 0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(),
                    "could not protect SELinux relabel lock " + fullPath);
            }

            while (NativeMethods.Flock(
                       descriptor,
                       NativeMethods.LockExclusive | NativeMethods.LockNonBlocking) != 0)
            {
                var error = Marshal.GetLastPInvokeError();
                if (error is not (NativeMethods.ErrorAgain or NativeMethods.ErrorInterrupted))
                {
                    throw new Win32Exception(error,
                        "could not lock SELinux relabel state " + fullPath);
                }

                await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
            }

            return new RelabelLock(handle, descriptor, processLock);
        }
        catch
        {
            handle?.Dispose();
            _ = processLock.Release();
            throw;
        }
    }

    private static void RequireRegularSingleLinkLock(int descriptor, string path)
    {
        var buffer = Marshal.AllocHGlobal(NativeMethods.StatxBufferSize);
        try
        {
            if (NativeMethods.Statx(
                    descriptor,
                    string.Empty,
                    NativeMethods.AtEmptyPath,
                    NativeMethods.StatxBasicStats,
                    buffer) != 0)
            {
                throw new Win32Exception(
                    Marshal.GetLastPInvokeError(),
                    "could not inspect SELinux relabel lock " + path);
            }

            var mode = unchecked((ushort)Marshal.ReadInt16(buffer, NativeMethods.StatxModeOffset));
            var linkCount = unchecked((uint)Marshal.ReadInt32(buffer, NativeMethods.StatxLinkCountOffset));
            if ((mode & NativeMethods.FileTypeMask) != NativeMethods.RegularFileType || linkCount != 1)
            {
                throw new InvalidOperationException(
                    "SELinux relabel lock must be a single-link regular file: " + path);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void ValidateCoordinatorPaths(Paths paths)
    {
        RequireNonemptyRealDirectory(paths.ImmutableSeed, "immutable SELinux store seed");
        RequireRealDirectory(Path.GetDirectoryName(Path.GetFullPath(paths.RuntimeStore))!,
            "runtime SELinux store parent");
        RequireNonemptyRealDirectory(paths.Contexts, "SELinux file-context tree");
        RequireNonemptyRealDirectory(paths.Policy, "SELinux compiled-policy tree");
        RequireRealDirectory(Path.GetDirectoryName(Path.GetFullPath(paths.Epoch))!,
            "SELinux relabel epoch parent");
        RequireRealDirectory(Path.GetDirectoryName(Path.GetFullPath(paths.PersistentMarker))!,
            "persistent SELinux marker parent");
        RefuseSymbolicLink(paths.RuntimeStore, "runtime SELinux store");
        RefuseSymbolicLink(paths.Epoch, "SELinux relabel epoch");
        RefuseSymbolicLink(paths.PersistentMarker, "persistent SELinux marker");
    }

    private static void RequireNonemptyRealDirectory(string path, string label)
    {
        RequireRealDirectory(path, label);
        if (!Directory.EnumerateFileSystemEntries(path).Any())
        {
            throw new InvalidOperationException(label + " is empty: " + path);
        }
    }

    private static void RequireRealDirectory(string path, string label)
    {
        var fullPath = Path.GetFullPath(path);
        RequireNoSymbolicComponents(fullPath, label);
        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException(label + " is missing: " + fullPath);
        }
    }

    private static void RequireRealEntry(string path, string label)
    {
        var fullPath = Path.GetFullPath(path);
        RequireNoSymbolicComponents(fullPath, label);
        if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
        {
            throw new FileNotFoundException(label + " is missing", fullPath);
        }
    }

    private static void RequireNoSymbolicComponents(string path, string label)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath)
            ?? throw new InvalidOperationException(label + " path is not rooted: " + fullPath);
        var relative = Path.GetRelativePath(root, fullPath);
        var current = root;
        foreach (var component in relative.Split(
                     Path.DirectorySeparatorChar,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            if (new DirectoryInfo(current).LinkTarget is not null ||
                new FileInfo(current).LinkTarget is not null)
            {
                throw new InvalidOperationException(label + " must not contain symbolic links: " + current);
            }
        }
    }

    private static void RequireRegularFile(string path, string label)
    {
        RequireNoSymbolicComponents(path, label);
        var attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
        {
            throw new InvalidOperationException(label + " must be a regular file: " + path);
        }

        if (new FileInfo(path).Length > MarkerMaximumBytes)
        {
            throw new InvalidDataException(label + " is unexpectedly large: " + path);
        }
    }

    private static void RefuseSymbolicLink(string path, string label)
    {
        if (new DirectoryInfo(path).LinkTarget is not null || new FileInfo(path).LinkTarget is not null)
        {
            throw new InvalidOperationException(label + " must not be a symbolic link: " + path);
        }
    }

    private static bool EntryExists(string path)
        => File.Exists(path) || Directory.Exists(path) ||
           new FileInfo(path).LinkTarget is not null || new DirectoryInfo(path).LinkTarget is not null;

    private static string[] ReadMarkerLines(string path)
    {
        var contents = File.ReadAllText(path, Encoding.UTF8);
        if (!contents.EndsWith('\n') || contents.Contains('\r', StringComparison.Ordinal))
        {
            throw new InvalidDataException("SELinux marker must use canonical LF-terminated text");
        }

        var lines = contents[..^1].Split('\n', StringSplitOptions.None);
        if (lines.Any(string.IsNullOrEmpty))
        {
            throw new InvalidDataException("SELinux marker must not contain empty lines");
        }

        return lines;
    }

    private static string RequireField(string line, string name)
    {
        var prefix = name + "=";
        if (!line.StartsWith(prefix, StringComparison.Ordinal) || line.Length == prefix.Length)
        {
            throw new InvalidDataException("SELinux marker is missing " + name);
        }

        return line[prefix.Length..];
    }

    private static void RequireSha256(string value, string label)
    {
        if (value.Length != 64 || value.Any(character =>
                !char.IsAsciiHexDigit(character) || char.IsAsciiLetterUpper(character)))
        {
            throw new InvalidDataException(label + " must be 64 lowercase hexadecimal characters");
        }
    }

    private static void RequireGeneration(string value)
    {
        if (value.Length != 32 || value.Any(character =>
                !char.IsAsciiHexDigit(character) || char.IsAsciiLetterUpper(character)))
        {
            throw new InvalidDataException(
                "SELinux relabel generation must be 32 lowercase hexadecimal characters");
        }
    }

    private static void AppendDirectory(
        IncrementalHash digest,
        string root,
        string directory,
        string relative)
    {
        AppendText(digest, $"D\0{relative}\0{UnixMode(directory):X}\n");
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal))
        {
            var entryRelative = relative + "/" + Path.GetFileName(entry);
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException(
                    "SELinux label-contract trees must not contain symbolic links: " + entry);
            }
            else if ((attributes & FileAttributes.Directory) != 0)
            {
                AppendDirectory(digest, root, entry, entryRelative);
            }
            else
            {
                var info = new FileInfo(entry);
                AppendText(digest, $"F\0{entryRelative}\0{UnixMode(entry):X}\0{info.Length}\n");
                using var stream = File.OpenRead(entry);
                var buffer = new byte[64 * 1024];
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    digest.AppendData(buffer.AsSpan(0, read));
                }
            }
        }
    }

    private static int UnixMode(string path)
        => OperatingSystem.IsWindows() ? 0 : (int)File.GetUnixFileMode(path);

    private static void AppendText(IncrementalHash digest, string value)
        => digest.AppendData(Encoding.UTF8.GetBytes(value));

    private static string UnescapeMountInfo(string value)
        => value
            .Replace("\\040", " ", StringComparison.Ordinal)
            .Replace("\\011", "\t", StringComparison.Ordinal)
            .Replace("\\012", "\n", StringComparison.Ordinal)
            .Replace("\\134", "\\", StringComparison.Ordinal);

    private static void FsyncDirectory(string path)
    {
        var descriptor = NativeMethods.Open(
            path,
            NativeMethods.OpenReadOnly | NativeMethods.OpenDirectory | NativeMethods.OpenCloseOnExec,
            0);
        if (descriptor < 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(),
                "could not open SELinux marker directory " + path);
        }

        try
        {
            if (NativeMethods.Fsync(descriptor) != 0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(),
                    "could not persist SELinux marker directory " + path);
            }
        }
        finally
        {
            _ = NativeMethods.Close(descriptor);
        }
    }

    private static void RequireDefaultEnvironment()
    {
        if (!OperatingSystem.IsLinux() ||
            !string.Equals(Environment.UserName, "root", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("SELinux relabel coordination must run as root on Linux");
        }
    }

    private sealed record SelinuxRelabelMarker(
        int SchemaVersion,
        string Scope,
        string StoreSha256,
        string LabelContractSha256,
        string Generation);

    private sealed class RelabelLock(
        SafeFileHandle handle,
        int descriptor,
        SemaphoreSlim processLock) : IDisposable, IAsyncDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _ = NativeMethods.Flock(descriptor, NativeMethods.LockUnlock);
            handle.Dispose();
            _ = processLock.Release();
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private static partial class NativeMethods
    {
        internal const int OpenReadOnly = 0;
        internal const int OpenReadWrite = 2;
        internal const int OpenCreate = 0x40;
        internal const int OpenCloseOnExec = 0x80000;
        internal const int OpenNoFollow = 0x20000;
        internal const int OpenDirectory = 0x10000;
        internal const int LockExclusive = 2;
        internal const int LockNonBlocking = 4;
        internal const int LockUnlock = 8;
        internal const int ErrorInterrupted = 4;
        internal const int ErrorAgain = 11;
        internal const int AtEmptyPath = 0x1000;
        internal const uint StatxBasicStats = 0x7ff;
        internal const int StatxBufferSize = 256;
        internal const int StatxLinkCountOffset = 16;
        internal const int StatxModeOffset = 28;
        internal const int FileTypeMask = 0xf000;
        internal const int RegularFileType = 0x8000;

        [LibraryImport(
            "libc",
            EntryPoint = "open",
            SetLastError = true,
            StringMarshalling = StringMarshalling.Utf8)]
        internal static partial int Open(
            string path,
            int flags,
            uint mode);

        [LibraryImport(
            "libc",
            EntryPoint = "statx",
            SetLastError = true,
            StringMarshalling = StringMarshalling.Utf8)]
        internal static partial int Statx(
            int directoryFileDescriptor,
            string path,
            int flags,
            uint mask,
            nint buffer);

        [LibraryImport("libc", EntryPoint = "flock", SetLastError = true)]
        internal static partial int Flock(int descriptor, int operation);

        [LibraryImport("libc", EntryPoint = "fchmod", SetLastError = true)]
        internal static partial int Fchmod(int descriptor, uint mode);

        [LibraryImport("libc", EntryPoint = "fsync", SetLastError = true)]
        internal static partial int Fsync(int descriptor);

        [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
        internal static partial int Close(int descriptor);
    }
}
