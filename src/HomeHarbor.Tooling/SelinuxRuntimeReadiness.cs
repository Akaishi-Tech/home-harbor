using System.Globalization;
using System.Runtime.InteropServices;

namespace HomeHarbor.Tooling;

public static partial class SelinuxRuntimeReadiness
{
    public const string EnforcePath = "/sys/fs/selinux/enforce";

    private const int AtFileSystemWorkingDirectory = -100;
    private const int AtSymlinkNoFollow = 0x100;
    private const uint StatxBasicStats = 0x7ff;
    private const int StatxBufferSize = 256;
    private const int StatxUidOffset = 20;
    private const int StatxGidOffset = 24;
    private const int StatxModeOffset = 28;
    private const int DirectoryFileType = 0x4000;
    private const int FileTypeMask = 0xf000;
    private const int PermissionMask = 0x0fff;

    internal sealed record RequiredDirectory(string Path, string Mode, string Owner, string Group);

    internal sealed record DirectoryMetadata(int Mode, uint Uid, uint Gid);

    internal static IReadOnlyList<RequiredDirectory> SystemDirectories { get; } =
    [
        new("/var/lib/homeharbor", "0750", "root", "homeharbor"),
        new("/var/lib/homeharbor/ota", "0750", "root", "homeharbor"),
        new("/var/lib/homeharbor/api", "0700", "homeharbor", "homeharbor"),
        new("/var/lib/homeharbor/setup", "0750", "root", "homeharbor"),
        new("/var/lib/homeharbor/bootloop", "0750", "root", "root"),
        new("/var/lib/homeharbor/secrets", "0700", "root", "root"),
        new("/var/lib/homeharbor/samba", "0750", "root", "root"),
        new("/var/lib/homeharbor/samba/private", "0750", "root", "root"),
        new("/var/lib/homeharbor/samba/state", "0750", "root", "root"),
        new("/var/lib/homeharbor/samba/cache", "0750", "root", "root"),
        new("/var/lib/homeharbor/samba/lock", "0750", "root", "root"),
        new("/var/lib/homeharbor-containers", "0750", "root", "homeharbor-containers"),
        new("/var/lib/homeharbor-containers/.config", "0750", "root", "homeharbor-containers"),
        new("/var/lib/homeharbor-containers/.config/containers", "0750", "root", "homeharbor-containers"),
        new("/var/lib/homeharbor-containers/.config/containers/systemd", "0750", "root", "homeharbor-containers"),
        new("/var/lib/homeharbor-containers/.config/containers/runtime", "0700", "homeharbor-containers", "homeharbor-containers"),
        new("/var/lib/homeharbor-containers/.local", "0750", "root", "homeharbor-containers"),
        new("/var/lib/homeharbor-containers/.local/share", "0750", "root", "homeharbor-containers"),
        new("/var/lib/homeharbor-containers/.local/share/containers", "0750", "homeharbor-containers", "homeharbor-containers"),
        new("/var/lib/homeharbor-containers/.cache", "0750", "root", "homeharbor-containers"),
        new("/var/lib/homeharbor-containers/.cache/containers", "0750", "homeharbor-containers", "homeharbor-containers"),
        new("/var/lib/caddy", "0750", "caddy", "caddy"),
        new("/var/lib/homeharbor-caddy", "0750", "root", "caddy"),
        new("/var/log/audit", "0700", "root", "root"),
        new("/var/lib/NetworkManager", "0755", "root", "root"),
        new("/var/lib/containers", "0755", "root", "root"),
        new("/run/homeharbor", "0750", "homeharbor", "homeharbor"),
        new("/run/homeharbor-api", "2750", "homeharbor", "homeharbor-api"),
        new("/run/homeharbor-smb-credentials", "0700", "homeharbor", "homeharbor")
    ];

    internal static IReadOnlyList<RequiredDirectory> RecoveryDirectories { get; } =
    [
        new("/var/lib/homeharbor", "0711", "root", "root"),
        new("/var/lib/homeharbor/recovery", "0750", "recovery", "recovery"),
        new("/var/log/audit", "0700", "root", "root"),
        new("/run/homeharbor-recovery", "0755", "recovery", "recovery"),
        new("/homeharbor-data", "0750", "root", "root")
    ];

    public static void RequireEnforcingDefault()
        => RequireEnforcing(EnforcePath);

    public static void RequireDefault()
        => RequireRoot("/", verifySelinuxContexts: true);

    internal static void RequireRoot(string root, bool verifySelinuxContexts = false)
    {
        var fullRoot = Path.GetFullPath(root);
        RequireEnforcing(UnderRoot(fullRoot, EnforcePath));
        var identities = IdentityMaps.Load(fullRoot);

        IReadOnlyList<RequiredDirectory> requirements =
            File.Exists(UnderRoot(fullRoot, "/usr/lib/homeharbor/api/HomeHarbor.Api"))
                ? SystemDirectories
                : File.Exists(UnderRoot(fullRoot, "/usr/lib/homeharbor/recovery/HomeHarbor.Recovery"))
                    ? RecoveryDirectories
                    : throw new InvalidOperationException(
                        "could not identify the HomeHarbor system or recovery SELinux readiness profile");

        foreach (var requirement in requirements)
        {
            RequireDirectory(
                UnderRoot(fullRoot, requirement.Path),
                requirement.Mode,
                identities.RequireUser(requirement.Owner),
                identities.RequireGroup(requirement.Group),
                verifySelinuxContexts);
        }
    }

    internal static void RequireEnforcing(string path)
    {
        if (!File.Exists(path) || !string.Equals(File.ReadAllText(path).Trim(), "1", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("SELinux must be enabled and enforcing: " + path);
        }
    }

    internal static void RequireDirectory(
        string path,
        string expectedMode,
        uint expectedUid,
        uint expectedGid,
        bool verifySelinuxContext)
    {
        var info = new DirectoryInfo(path);
        if (!info.Exists || info.LinkTarget is not null)
        {
            throw new InvalidOperationException("required HomeHarbor runtime directory is missing or symbolic: " + path);
        }

        var expected = Convert.ToInt32(expectedMode, 8);
        var actual = ReadDirectoryMetadata(path);
        if (actual.Mode != expected)
        {
            throw new InvalidOperationException(
                $"required HomeHarbor runtime directory {path} has mode {Convert.ToString(actual.Mode, 8)}, expected {expectedMode}");
        }

        if (actual.Uid != expectedUid || actual.Gid != expectedGid)
        {
            throw new InvalidOperationException(
                $"required HomeHarbor runtime directory {path} has ownership {actual.Uid}:{actual.Gid}, expected {expectedUid}:{expectedGid}");
        }

        if (verifySelinuxContext)
        {
            RequireMatchingSelinuxContext(path);
        }
    }

    internal static DirectoryMetadata ReadDirectoryMetadata(string path)
    {
        var buffer = Marshal.AllocHGlobal(StatxBufferSize);
        try
        {
            if (NativeMethods.Statx(
                    AtFileSystemWorkingDirectory,
                    path,
                    AtSymlinkNoFollow,
                    StatxBasicStats,
                    buffer) != 0)
            {
                throw new InvalidOperationException(
                    $"could not inspect required HomeHarbor runtime directory {path}: errno {Marshal.GetLastPInvokeError()}");
            }

            var mode = unchecked((ushort)Marshal.ReadInt16(buffer, StatxModeOffset));
            if ((mode & FileTypeMask) != DirectoryFileType)
            {
                throw new InvalidOperationException("required HomeHarbor runtime path is not a directory: " + path);
            }

            return new DirectoryMetadata(
                mode & PermissionMask,
                unchecked((uint)Marshal.ReadInt32(buffer, StatxUidOffset)),
                unchecked((uint)Marshal.ReadInt32(buffer, StatxGidOffset)));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    internal static void RequireMatchingSelinuxContext(string path)
    {
        var actual = ReadContext(path, NativeMethods.LGetFileCon, "read the current SELinux context");
        var expected = ReadContext(path, NativeMethods.MatchPathCon, "resolve the expected SELinux context");
        RequireMatchingSelinuxContext(path, actual, expected);
    }

    internal static void RequireMatchingSelinuxContext(string path, string actual, string expected)
    {
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"required HomeHarbor runtime directory {path} has SELinux context {actual}, expected {expected}");
        }
    }

    private static string ReadContext(
        string path,
        NativeMethods.ContextReader reader,
        string operation)
    {
        var result = reader(path, DirectoryFileType, out var context);
        try
        {
            if (result < 0 || context == IntPtr.Zero)
            {
                throw new InvalidOperationException(
                    $"could not {operation} for {path}: errno {Marshal.GetLastPInvokeError()}");
            }

            return Marshal.PtrToStringUTF8(context)
                ?? throw new InvalidOperationException($"could not decode the SELinux context for {path}");
        }
        finally
        {
            if (context != IntPtr.Zero)
            {
                NativeMethods.FreeCon(context);
            }
        }
    }

    private static string UnderRoot(string root, string absolutePath)
        => Path.Combine(root, absolutePath.TrimStart('/'));

    private sealed record IdentityMaps(
        IReadOnlyDictionary<string, uint> Users,
        IReadOnlyDictionary<string, uint> Groups)
    {
        public static IdentityMaps Load(string root)
            => new(
                LoadMap(Path.Combine(root, "etc", "passwd"), 2, "user"),
                LoadMap(Path.Combine(root, "etc", "group"), 2, "group"));

        public uint RequireUser(string name)
            => Require(Users, name, "user");

        public uint RequireGroup(string name)
            => Require(Groups, name, "group");

        private static IReadOnlyDictionary<string, uint> LoadMap(string path, int idField, string label)
        {
            if (!File.Exists(path))
            {
                throw new InvalidOperationException($"required identity database is missing: {path}");
            }

            var result = new Dictionary<string, uint>(StringComparer.Ordinal);
            foreach (var rawLine in File.ReadLines(path))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith('#'))
                {
                    continue;
                }

                var fields = line.Split(':');
                if (fields.Length <= idField || string.IsNullOrWhiteSpace(fields[0]) ||
                    !uint.TryParse(fields[idField], NumberStyles.None, CultureInfo.InvariantCulture, out var id))
                {
                    throw new InvalidOperationException($"invalid {label} identity entry in {path}: {rawLine}");
                }

                if (!result.TryAdd(fields[0], id))
                {
                    throw new InvalidOperationException($"duplicate {label} identity {fields[0]} in {path}");
                }
            }

            return result;
        }

        private static uint Require(IReadOnlyDictionary<string, uint> identities, string name, string label)
            => identities.TryGetValue(name, out var id)
                ? id
                : throw new InvalidOperationException($"required HomeHarbor {label} identity is missing: {name}");
    }

    private static partial class NativeMethods
    {
        internal delegate int ContextReader(string path, int mode, out IntPtr context);

        [LibraryImport("libc", EntryPoint = "statx", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
        internal static partial int Statx(int directoryFileDescriptor, string path, int flags, uint mask, IntPtr buffer);

        internal static int LGetFileCon(string path, int _, out IntPtr context)
            => LGetFileConNative(path, out context);

        internal static int MatchPathCon(string path, int mode, out IntPtr context)
            => MatchPathConNative(path, mode, out context);

        [LibraryImport("libselinux.so.1", EntryPoint = "lgetfilecon", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
        private static partial int LGetFileConNative(string path, out IntPtr context);

        [LibraryImport("libselinux.so.1", EntryPoint = "matchpathcon", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
        private static partial int MatchPathConNative(string path, int mode, out IntPtr context);

        [LibraryImport("libselinux.so.1", EntryPoint = "freecon")]
        internal static partial void FreeCon(IntPtr context);
    }
}
