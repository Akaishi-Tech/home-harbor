namespace HomeHarbor.Tooling;

internal static class FileTreeCopier
{
    public static void CopyDirectory(string source, string destination)
    {
        if (!Directory.Exists(source))
        {
            throw new DirectoryNotFoundException(source);
        }

        _ = Directory.CreateDirectory(destination);
        File.SetUnixFileMode(destination, File.GetUnixFileMode(source));

        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
        {
            CopyEntry(entry, Path.Combine(destination, Path.GetFileName(entry)));
        }
    }

    private static void CopyEntry(string source, string destination)
    {
        var attributes = File.GetAttributes(source);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            CopySymbolicLink(source, destination, (attributes & FileAttributes.Directory) != 0);
            return;
        }

        if ((attributes & FileAttributes.Directory) != 0)
        {
            CopyDirectory(source, destination);
            return;
        }

        _ = Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(source, destination, overwrite: true);
        File.SetUnixFileMode(destination, File.GetUnixFileMode(source));
    }

    private static void CopySymbolicLink(string source, string destination, bool directory)
    {
        var linkTarget = directory
            ? new DirectoryInfo(source).LinkTarget
            : new FileInfo(source).LinkTarget;
        if (string.IsNullOrWhiteSpace(linkTarget))
        {
            throw new IOException("could not read symbolic link target for " + source);
        }

        _ = Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        DeleteExisting(destination);
        _ = directory ? Directory.CreateSymbolicLink(destination, linkTarget) : File.CreateSymbolicLink(destination, linkTarget);
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
