using System.Formats.Tar;
using System.IO.Compression;

namespace HomeHarbor.Tooling;

public static class SystemAppPayloadExtractor
{
    public static async Task ExtractTarGzAsync(
        string archivePath,
        string destinationRoot,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(archivePath))
        {
            throw new FileNotFoundException("system app payload not found", archivePath);
        }

        if (Directory.Exists(destinationRoot))
        {
            Directory.Delete(destinationRoot, recursive: true);
        }

        _ = Directory.CreateDirectory(destinationRoot);
        await using var file = File.OpenRead(archivePath);
        await using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new TarReader(gzip);

        var extractedAny = false;
        while (await reader.GetNextEntryAsync(copyData: false, cancellationToken) is { } entry)
        {
            var relativePath = NormalizeMemberPath(entry.Name);
            if (relativePath.Length == 0)
            {
                continue;
            }

            var payloadRelativePath = SystemAppPayloadBuilder.ToPayloadRelativePath("/" + relativePath);
            var destination = CheckedCombine(destinationRoot, payloadRelativePath);
            extractedAny = true;

            switch (entry.EntryType)
            {
                case TarEntryType.Directory:
                    _ = Directory.CreateDirectory(destination);
                    ApplyMode(destination, entry.Mode);
                    break;
                case TarEntryType.RegularFile:
                case TarEntryType.V7RegularFile:
                    if (entry.DataStream is null)
                    {
                        throw new InvalidOperationException($"system app payload file has no data stream: {entry.Name}");
                    }

                    _ = Directory.CreateDirectory(Path.GetDirectoryName(destination) ?? destinationRoot);
                    await using (var output = File.Create(destination))
                    {
                        await entry.DataStream.CopyToAsync(output, cancellationToken);
                    }

                    ApplyMode(destination, entry.Mode);
                    break;
                case TarEntryType.SymbolicLink:
                    SystemAppPayloadBuilder.ValidateSymlinkTarget(payloadRelativePath, entry.LinkName);
                    _ = Directory.CreateDirectory(Path.GetDirectoryName(destination) ?? destinationRoot);
                    DeleteExistingPath(destination);
                    _ = File.CreateSymbolicLink(destination, entry.LinkName);
                    break;
                default:
                    throw new InvalidOperationException($"system app payload member type is not allowed: {entry.EntryType} {entry.Name}");
            }
        }

        if (!extractedAny || !Directory.Exists(Path.Combine(destinationRoot, "usr")))
        {
            throw new InvalidOperationException("system app payload must contain a usr directory");
        }
    }

    private static string NormalizeMemberPath(string member)
    {
        TarSafety.ValidateMemberPath(member, "system app payload");
        var path = member.Trim().TrimEnd('/');
        if (path.StartsWith("./", StringComparison.Ordinal))
        {
            path = path[2..];
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
}
