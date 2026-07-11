internal static class RootPathGuard
{
    internal static string RequireNoSymlinkComponents(
        string path,
        string label,
        bool requireLeafDirectory = false)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException(label + " path is empty");
        }

        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath)
            ?? throw new InvalidOperationException(label + " path has no filesystem root");
        if (string.Equals(fullPath, root, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(label + " must not be the filesystem root");
        }

        var current = root;
        var segments = fullPath[root.Length..]
            .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < segments.Length; index++)
        {
            current = Path.Combine(current, segments[index]);
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(current);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                continue;
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException(label + " contains a symbolic link: " + current);
            }
            if (index < segments.Length - 1 && (attributes & FileAttributes.Directory) == 0)
            {
                throw new InvalidOperationException(label + " has a non-directory path component: " + current);
            }
            if (requireLeafDirectory && index == segments.Length - 1 && (attributes & FileAttributes.Directory) == 0)
            {
                throw new InvalidOperationException(label + " is not a directory: " + current);
            }
        }

        return fullPath;
    }

    internal static string CreateDirectory(string path, string label)
    {
        var fullPath = RequireNoSymlinkComponents(path, label);
        _ = Directory.CreateDirectory(fullPath);
        return RequireNoSymlinkComponents(fullPath, label, requireLeafDirectory: true);
    }

    internal static string RequireChildPath(string path, string root, string label)
    {
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        var fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(label + " must stay inside " + rootFull);
        }
        _ = RequireNoSymlinkComponents(rootFull, label + " root");
        return RequireNoSymlinkComponents(fullPath, label);
    }

    internal static void RequireSystemAppRoots(string root, string wrapperRoot)
    {
        var systemRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        var wrapper = Path.GetFullPath(wrapperRoot).TrimEnd(Path.DirectorySeparatorChar);
        if (!string.Equals(Path.GetFileName(systemRoot), "system-apps", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("system app root must end in a dedicated system-apps directory");
        }
        if (!string.Equals(Path.GetFileName(wrapper), "bin", StringComparison.Ordinal) ||
            !string.Equals(Path.GetFileName(Path.GetDirectoryName(wrapper)), "system-apps", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("system app wrapper root must end in a dedicated system-apps/bin directory");
        }

        _ = RequireNoSymlinkComponents(systemRoot, "system app root");
        _ = RequireNoSymlinkComponents(wrapper, "system app wrapper root");
    }
}
