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
        var areaRoot = Path.GetFullPath(Path.Combine(dataRoot, "families", familyId.ToString("N"), AreaDirectoryName(area)));
        var candidate = Path.GetFullPath(Path.Combine(areaRoot, relative));

        return !candidate.Equals(areaRoot, StringComparison.Ordinal) &&
            !candidate.StartsWith(areaRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            ? throw new InvalidOperationException("Path escapes the storage root.")
            : candidate;
    }

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
