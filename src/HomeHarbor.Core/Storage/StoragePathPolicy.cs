namespace HomeHarbor.Core.Storage;

public static class StoragePathPolicy
{
    public static string AreaDirectoryName(StorageArea area) => area switch
    {
        StorageArea.Files => "files",
        StorageArea.Photos => "photos",
        StorageArea.Backups => "backups",
        _ => throw new ArgumentOutOfRangeException(nameof(area), area, "Unknown storage area.")
    };

    public static string NormalizeDavPath(string? rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath)) return "/";

        ValidatePercentEncoding(rawPath);

        string path;
        try
        {
            path = Uri.UnescapeDataString(rawPath).Replace('\\', '/');
        }
        catch (UriFormatException ex)
        {
            throw new InvalidOperationException("Path contains malformed percent-encoding.", ex);
        }

        if (path.Contains('\0'))
            throw new InvalidOperationException("Null bytes are not allowed in paths.");

        if (!path.StartsWith('/')) path = "/" + path;

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment is "." or ".."))
            throw new InvalidOperationException("Path traversal segments are not allowed.");

        var normalized = "/" + string.Join('/', segments);
        return normalized == "/" ? "/" : normalized.TrimEnd('/');
    }

    public static string ToSafeRelativePath(string normalizedPath)
    {
        return normalizedPath == "/"
            ? string.Empty
            : !normalizedPath.StartsWith('/')
            ? throw new InvalidOperationException("Normalized paths must start with '/'.")
            : normalizedPath[1..];
    }

    public static string ResolvePhysicalPath(string dataRoot, Guid familyId, StorageArea area, string normalizedPath)
    {
        var relative = ToSafeRelativePath(normalizedPath);
        var fullDataRoot = Path.GetFullPath(dataRoot);
        var areaRoot = Path.GetFullPath(Path.Combine(fullDataRoot, "families", familyId.ToString("N"), AreaDirectoryName(area)));
        var candidate = Path.GetFullPath(Path.Combine(areaRoot, relative));

        if (!candidate.Equals(areaRoot, PathComparison) &&
            !candidate.StartsWith(areaRoot + Path.DirectorySeparatorChar, PathComparison))
        {
            throw new InvalidOperationException("Path escapes the storage root.");
        }

        RejectReparsePoints(fullDataRoot, candidate);
        return candidate;
    }

    public static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }

    private static void RejectReparsePoints(string root, string candidate)
    {
        RejectReparsePoint(root);
        var relative = Path.GetRelativePath(root, candidate);
        var current = root;
        foreach (var segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            RejectReparsePoint(current);
        }
    }

    private static void RejectReparsePoint(string path)
    {
        if (IsReparsePoint(path))
            throw new InvalidOperationException("Symbolic links and reparse points are not allowed in storage paths.");
    }

    private static StringComparison PathComparison
        => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static void ValidatePercentEncoding(string rawPath)
    {
        for (var i = 0; i < rawPath.Length; i++)
        {
            if (rawPath[i] != '%') continue;

            if (i + 2 >= rawPath.Length ||
                !Uri.IsHexDigit(rawPath[i + 1]) ||
                !Uri.IsHexDigit(rawPath[i + 2]))
            {
                throw new InvalidOperationException("Path contains malformed percent-encoding.");
            }
        }
    }
}
