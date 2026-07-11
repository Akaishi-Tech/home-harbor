using System.Formats.Tar;
using System.IO.Compression;

namespace HomeHarbor.Tooling;

public static class SystemAppPayloadExtractor
{
    private const int MaxMembers = 25_000;
    private const long MaxFileBytes = 512L * 1024 * 1024;
    private const long MaxTotalBytes = 2L * 1024 * 1024 * 1024;
    private const int MaxPathLength = 4096;

    public static async Task ExtractTarGzAsync(
        string archivePath,
        string destinationRoot,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(archivePath))
        {
            throw new FileNotFoundException("system app payload not found", archivePath);
        }

        if (new FileInfo(archivePath).Length > MaxTotalBytes)
        {
            throw new InvalidOperationException($"system app payload archive exceeds the {MaxTotalBytes}-byte limit");
        }

        var destinationRootFull = Path.GetFullPath(destinationRoot);
        var fileSystemRoot = Path.GetPathRoot(destinationRootFull);
        if (string.IsNullOrEmpty(fileSystemRoot) ||
            string.Equals(destinationRootFull, fileSystemRoot, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("system app payload destination cannot be a filesystem root");
        }

        var parent = Path.GetDirectoryName(destinationRootFull)
            ?? throw new InvalidOperationException("system app payload destination has no parent directory");
        _ = Directory.CreateDirectory(parent);
        var extractionRoot = Path.Combine(parent, "." + Path.GetFileName(destinationRootFull) + ".extract-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(extractionRoot);
        File.SetUnixFileMode(extractionRoot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            await ExtractIntoAsync(archivePath, extractionRoot, cancellationToken);
            DeleteExistingPath(destinationRootFull);
            Directory.Move(extractionRoot, destinationRootFull);
        }
        finally
        {
            DeleteExistingPath(extractionRoot);
        }
    }

    private static async Task ExtractIntoAsync(
        string archivePath,
        string destinationRootFull,
        CancellationToken cancellationToken)
    {
        await using var file = File.OpenRead(archivePath);
        await using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new TarReader(gzip);

        var extractedAny = false;
        var memberCount = 0;
        long totalBytes = 0;
        var memberPaths = new HashSet<string>(StringComparer.Ordinal);
        var memberAncestorPaths = new HashSet<string>(StringComparer.Ordinal);
        var symlinkPaths = new HashSet<string>(StringComparer.Ordinal);
        var deferredSymlinks = new List<DeferredSymlink>();
        var deferredDirectoryModes = new List<(string Path, UnixFileMode Mode)>();
        while (await reader.GetNextEntryAsync(copyData: false, cancellationToken) is { } entry)
        {
            memberCount++;
            if (memberCount > MaxMembers)
            {
                throw new InvalidOperationException($"system app payload has too many members; maximum is {MaxMembers}");
            }

            var relativePath = NormalizeMemberPath(entry.Name);
            if (relativePath.Length == 0)
            {
                continue;
            }

            var payloadRelativePath = SystemAppPayloadBuilder.ToPayloadRelativePath("/" + relativePath);
            if (!memberPaths.Add(payloadRelativePath))
            {
                throw new InvalidOperationException("system app payload contains a duplicate member: " + entry.Name);
            }

            RefuseSymlinkAncestor(payloadRelativePath, symlinkPaths);
            TrackAncestorPaths(payloadRelativePath, memberAncestorPaths);
            var destination = CheckedCombine(destinationRootFull, payloadRelativePath);
            extractedAny = true;

            switch (entry.EntryType)
            {
                case TarEntryType.Directory:
                    _ = Directory.CreateDirectory(destination);
                    deferredDirectoryModes.Add((destination, entry.Mode));
                    break;
                case TarEntryType.RegularFile:
                case TarEntryType.V7RegularFile:
                    if (entry.DataStream is null)
                    {
                        throw new InvalidOperationException($"system app payload file has no data stream: {entry.Name}");
                    }

                    if (entry.Length < 0 || entry.Length > MaxFileBytes || totalBytes > MaxTotalBytes - entry.Length)
                    {
                        throw new InvalidOperationException($"system app payload size limit exceeded by member: {entry.Name}");
                    }

                    totalBytes += entry.Length;
                    _ = Directory.CreateDirectory(Path.GetDirectoryName(destination) ?? destinationRootFull);
                    await using (var output = new FileStream(
                                     destination,
                                     FileMode.CreateNew,
                                     FileAccess.Write,
                                     FileShare.None,
                                     1024 * 1024,
                                     FileOptions.Asynchronous | FileOptions.SequentialScan))
                    {
                        await CopyLimitedAsync(entry, output, cancellationToken);
                    }

                    ApplyMode(destination, entry.Mode);
                    break;
                case TarEntryType.SymbolicLink:
                    if (entry.LinkName.Length > MaxPathLength || entry.LinkName.Any(char.IsControl))
                    {
                        throw new InvalidOperationException($"system app payload symlink target is invalid: {entry.Name}");
                    }

                    SystemAppPayloadBuilder.ValidateSymlinkTarget(payloadRelativePath, entry.LinkName);
                    if (memberAncestorPaths.Contains(payloadRelativePath))
                    {
                        throw new InvalidOperationException($"system app payload symlink conflicts with child members: {entry.Name}");
                    }

                    _ = symlinkPaths.Add(payloadRelativePath);
                    deferredSymlinks.Add(new DeferredSymlink(payloadRelativePath, destination, entry.LinkName));
                    break;
                default:
                    throw new InvalidOperationException($"system app payload member type is not allowed: {entry.EntryType} {entry.Name}");
            }
        }

        foreach (var symlink in deferredSymlinks)
        {
            RefuseSymlinkAncestor(symlink.RelativePath, symlinkPaths, ignoreSelf: true);
            _ = Directory.CreateDirectory(Path.GetDirectoryName(symlink.Destination) ?? destinationRootFull);
            DeleteExistingPath(symlink.Destination);
            _ = File.CreateSymbolicLink(symlink.Destination, symlink.Target);
        }

        foreach (var (path, mode) in deferredDirectoryModes.OrderByDescending(item => item.Path.Length))
        {
            ApplyMode(path, mode);
        }

        if (!extractedAny || !Directory.Exists(Path.Combine(destinationRootFull, "usr")))
        {
            throw new InvalidOperationException("system app payload must contain a usr directory");
        }
    }

    private static string NormalizeMemberPath(string member)
    {
        if (member.Length > MaxPathLength || member.Any(char.IsControl))
        {
            throw new InvalidOperationException("system app payload member path is too long or contains control characters");
        }

        TarSafety.ValidateMemberPath(member, "system app payload");
        var path = member.TrimEnd('/');
        if (path.StartsWith("./", StringComparison.Ordinal))
        {
            path = path[2..];
        }

        if (path.Split('/').Any(segment => segment.Length > 255))
        {
            throw new InvalidOperationException($"system app payload member contains an oversized path component: {member}");
        }

        return path == "usr" || path.StartsWith("usr/", StringComparison.Ordinal)
            ? path
            : throw new InvalidOperationException($"system app payload member must stay under usr: {member}");
    }

    private static string CheckedCombine(string root, string relativePath)
    {
        var combined = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        return !combined.Equals(root, StringComparison.Ordinal) &&
            !combined.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            ? throw new InvalidOperationException($"system app payload path escapes root: {relativePath}")
            : combined;
    }

    private static void RefuseSymlinkAncestor(
        string relativePath,
        IReadOnlySet<string> symlinkPaths,
        bool ignoreSelf = false)
    {
        var current = relativePath;
        while (true)
        {
            if ((!ignoreSelf || !string.Equals(current, relativePath, StringComparison.Ordinal)) &&
                symlinkPaths.Contains(current))
            {
                throw new InvalidOperationException($"system app payload member traverses symlink {current}: {relativePath}");
            }

            var separator = current.LastIndexOf('/');
            if (separator < 0)
            {
                return;
            }

            current = current[..separator];
        }
    }

    private static void TrackAncestorPaths(string relativePath, ISet<string> ancestors)
    {
        var current = relativePath;
        while (true)
        {
            var separator = current.LastIndexOf('/');
            if (separator < 0)
            {
                return;
            }

            current = current[..separator];
            _ = ancestors.Add(current);
        }
    }

    private static async Task CopyLimitedAsync(TarEntry entry, Stream output, CancellationToken cancellationToken)
    {
        var buffer = new byte[1024 * 1024];
        long copied = 0;
        int read;
        while ((read = await entry.DataStream!.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
        {
            if (copied > entry.Length - read || copied > MaxFileBytes - read)
            {
                throw new InvalidOperationException($"system app payload member exceeds its declared or allowed size: {entry.Name}");
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            copied += read;
        }

        if (copied != entry.Length)
        {
            throw new InvalidOperationException($"system app payload member size mismatch: {entry.Name}");
        }
    }

    private static void ApplyMode(string path, UnixFileMode mode)
    {
        if (mode == 0)
        {
            return;
        }

        File.SetUnixFileMode(path, mode & (UnixFileMode)0x1ff);
    }

    private static void DeleteExistingPath(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if (attributes.HasFlag(FileAttributes.Directory) && !attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                Directory.Delete(path, recursive: true);
                return;
            }

            File.Delete(path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
        }
    }

    private sealed record DeferredSymlink(string RelativePath, string Destination, string Target);
}
