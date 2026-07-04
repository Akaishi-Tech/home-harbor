using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace HomeHarbor.Tooling;

public static partial class SystemAppPayloadBuilder
{
    public const string SystemAppsRoot = "/homeharbor-data/system-apps/active";

    public static async Task CopyPacmanPackagesAsync(
        string rootfs,
        string appKey,
        string destination,
        IEnumerable<string> packageNames,
        CancellationToken cancellationToken = default)
    {
        _ = ValidateAppKey(appKey);
        var packages = packageNames
            .Select(ValidatePackageName)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (packages.Length == 0)
        {
            throw new InvalidOperationException("At least one package name is required for a system app payload.");
        }

        foreach (var packageName in packages)
        {
            var packagePaths = await ListPacmanPackageFilesAsync(rootfs, packageName, cancellationToken);
            CopyPackagePaths(rootfs, destination, packagePaths);
        }
    }

    public static void CopyPackagePaths(
        string rootfs,
        string destination,
        IEnumerable<string> packagePaths)
    {
        var sourceRoot = Path.GetFullPath(rootfs);
        var destinationRoot = Path.GetFullPath(destination);
        _ = Directory.CreateDirectory(destinationRoot);

        foreach (var packagePath in packagePaths
                     .Select(ToPayloadRelativePath)
                     .Distinct(StringComparer.Ordinal)
                     .OrderBy(path => path, StringComparer.Ordinal))
        {
            CopyPayloadPath(sourceRoot, destinationRoot, packagePath);
        }
    }

    public static string ToPayloadRelativePath(string packagePath)
    {
        if (string.IsNullOrWhiteSpace(packagePath))
        {
            throw new InvalidOperationException("Package path cannot be empty.");
        }

        var path = packagePath.Trim();
        if (path.Contains('\\', StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"System app payload paths must use POSIX separators: {packagePath}");
        }

        path = path.TrimEnd('/');
        if (path.Length == 0 || path[0] != '/')
        {
            throw new InvalidOperationException($"System app payload path must be absolute: {packagePath}");
        }

        if (path != "/usr" && !path.StartsWith("/usr/", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"System app payload path must stay under /usr: {packagePath}");
        }

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in segments)
        {
            if (segment is "." or "..")
            {
                throw new InvalidOperationException($"System app payload path cannot contain traversal segments: {packagePath}");
            }
        }

        return string.Join('/', segments);
    }

    public static void ValidateSymlinkTarget(string payloadRelativePath, string target)
    {
        if (string.IsNullOrEmpty(target))
        {
            throw new InvalidOperationException($"System app symlink target cannot be empty: {payloadRelativePath}");
        }

        if (target.Contains('\\', StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"System app symlink target must use POSIX separators: {payloadRelativePath}");
        }

        var parent = Path.GetDirectoryName(payloadRelativePath)?.Replace("\\", "/", StringComparison.Ordinal) ?? string.Empty;
        var normalized = target.StartsWith('/')
            ? NormalizeVirtualPath(target)
            : NormalizeVirtualPath("/" + parent + "/" + target);

        if (normalized != "/usr" && !normalized.StartsWith("/usr/", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"System app symlink target escapes /usr: {payloadRelativePath} -> {target}");
        }
    }

    private static void CopyPayloadPath(string sourceRoot, string destinationRoot, string payloadRelativePath)
    {
        var source = CheckedCombine(sourceRoot, payloadRelativePath);
        var destination = CheckedCombine(destinationRoot, payloadRelativePath);
        var kind = GetFileKind(source);

        if (kind == PayloadFileKind.Symlink)
        {
            var target = new FileInfo(source).LinkTarget ?? new DirectoryInfo(source).LinkTarget;
            if (string.IsNullOrEmpty(target))
            {
                throw new InvalidOperationException($"System app symlink target could not be read: {payloadRelativePath}");
            }

            ValidateSymlinkTarget(payloadRelativePath, target);
            EnsureParentDirectory(destination);
            DeleteExistingPath(destination);
            _ = File.CreateSymbolicLink(destination, target);
            return;
        }

        if (kind == PayloadFileKind.Directory)
        {
            _ = Directory.CreateDirectory(destination);
            File.SetUnixFileMode(destination, File.GetUnixFileMode(source));
            return;
        }

        if (kind == PayloadFileKind.RegularFile)
        {
            EnsureParentDirectory(destination);
            File.Copy(source, destination, overwrite: true);
            File.SetUnixFileMode(destination, File.GetUnixFileMode(source));
            return;
        }

        throw new InvalidOperationException($"System app payload path is not a regular file, directory, or symlink: {payloadRelativePath}");
    }

    private static async Task<IReadOnlyList<string>> ListPacmanPackageFilesAsync(
        string rootfs,
        string packageName,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "pacman",
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("--root");
        startInfo.ArgumentList.Add(rootfs);
        startInfo.ArgumentList.Add("-Qql");
        startInfo.ArgumentList.Add(packageName);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start pacman.");
        var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return process.ExitCode != 0
            ? throw new InvalidOperationException($"pacman could not list package files for {packageName}: {stderr.Trim()}")
            : (IReadOnlyList<string>)stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static string ValidateAppKey(string appKey)
    {
        var value = appKey.Trim();
        return !AppKeyRegex().IsMatch(value) ? throw new InvalidOperationException($"System app key is invalid: {appKey}") : value;
    }

    private static string ValidatePackageName(string packageName)
    {
        var value = packageName.Trim();
        return !PackageNameRegex().IsMatch(value)
            ? throw new InvalidOperationException($"Package name is invalid for a system app payload: {packageName}")
            : value;
    }

    private static string NormalizeVirtualPath(string path)
    {
        var stack = new Stack<string>();
        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            switch (segment)
            {
                case ".":
                    break;
                case "..":
                    if (!stack.TryPop(out _))
                    {
                        throw new InvalidOperationException($"Virtual path escapes root: {path}");
                    }

                    break;
                default:
                    stack.Push(segment);
                    break;
            }
        }

        return "/" + string.Join('/', stack.Reverse());
    }

    private static string CheckedCombine(string root, string relativePath)
    {
        var combined = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        return !combined.Equals(root, StringComparison.Ordinal) &&
            !combined.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            ? throw new InvalidOperationException($"System app payload path escapes root: {relativePath}")
            : combined;
    }

    private static void EnsureParentDirectory(string path)
    {
        var parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(parent))
        {
            _ = Directory.CreateDirectory(parent);
        }
    }

    private static void DeleteExistingPath(string path)
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return;
        }

        if (attributes.HasFlag(FileAttributes.Directory) && !attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            Directory.Delete(path, recursive: true);
            return;
        }

        File.Delete(path);
    }

    private static PayloadFileKind GetFileKind(string path)
    {
        if (LStat(path, out var stat) != 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "lstat failed for " + path);
        }

        return (stat.Mode & FileTypeMask) switch
        {
            FileTypeRegular => PayloadFileKind.RegularFile,
            FileTypeDirectory => PayloadFileKind.Directory,
            FileTypeSymlink => PayloadFileKind.Symlink,
            _ => PayloadFileKind.Special
        };
    }

    [LibraryImport("libc", EntryPoint = "lstat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int LStat(string path, out StatBuffer stat);

    private const uint FileTypeMask = 0xF000;
    private const uint FileTypeDirectory = 0x4000;
    private const uint FileTypeRegular = 0x8000;
    private const uint FileTypeSymlink = 0xA000;

    private enum PayloadFileKind
    {
        RegularFile,
        Directory,
        Symlink,
        Special
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StatBuffer
    {
        public ulong Device;
        public ulong Inode;
        public ulong HardLinks;
        public uint Mode;
        public uint UserId;
        public uint GroupId;
        public int Padding0;
        public ulong DeviceId;
        public long Size;
        public long BlockSize;
        public long Blocks;
        public Timespec AccessTime;
        public Timespec ModifyTime;
        public Timespec ChangeTime;
        public long Reserved0;
        public long Reserved1;
        public long Reserved2;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Timespec
    {
        public long Seconds;
        public long Nanoseconds;
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex AppKeyRegex();

    [GeneratedRegex("^[A-Za-z0-9@._+-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex PackageNameRegex();
}
