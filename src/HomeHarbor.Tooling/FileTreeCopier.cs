namespace HomeHarbor.Tooling;

internal static class FileTreeCopier
{
    public static void CopyDirectory(string source, string destination)
        => CopyDirectory(source, destination, _ => true);

    public static void CopyDirectory(string source, string destination, Func<string, bool> include)
    {
        if (!Directory.Exists(source))
        {
            throw new DirectoryNotFoundException(source);
        }

        _ = Directory.CreateDirectory(destination);
        File.SetUnixFileMode(destination, File.GetUnixFileMode(source));

        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
        {
            if (include(entry))
            {
                CopyEntry(entry, Path.Combine(destination, Path.GetFileName(entry)), include);
            }
        }
    }

    private static void CopyEntry(string source, string destination, Func<string, bool> include)
    {
        var attributes = File.GetAttributes(source);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            CopySymbolicLink(source, destination, (attributes & FileAttributes.Directory) != 0);
            return;
        }

        if ((attributes & FileAttributes.Directory) != 0)
        {
            CopyDirectory(source, destination, include);
            return;
        }

        _ = Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(source, destination, overwrite: true);
        File.SetUnixFileMode(destination, File.GetUnixFileMode(source));
    }

    private static void CopySymbolicLink(string source, string destination, bool directory)
    {
        var linkTarget = ReadSymbolicLink(source, directory);
        if (string.IsNullOrWhiteSpace(linkTarget))
        {
            throw new IOException("could not read symbolic link target for " + source);
        }

        _ = Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        DeleteExisting(destination);
        _ = directory ? Directory.CreateSymbolicLink(destination, linkTarget) : File.CreateSymbolicLink(destination, linkTarget);
    }

    internal static string? ReadSymbolicLink(string path, bool? directory = null)
    {
        var isDirectory = directory ?? (File.GetAttributes(path) & FileAttributes.Directory) != 0;
        return isDirectory
            ? new DirectoryInfo(path).LinkTarget ?? new FileInfo(path).LinkTarget
            : new FileInfo(path).LinkTarget ?? new DirectoryInfo(path).LinkTarget;
    }

    private static void DeleteExisting(string path)
    {
        var directoryInfo = new DirectoryInfo(path);
        if (directoryInfo.LinkTarget is not null)
        {
            Directory.Delete(path);
            return;
        }

        var fileInfo = new FileInfo(path);
        if (fileInfo.LinkTarget is not null || File.Exists(path))
        {
            File.Delete(path);
            return;
        }

        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
}
