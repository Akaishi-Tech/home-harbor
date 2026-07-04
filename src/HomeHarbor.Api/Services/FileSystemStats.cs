namespace HomeHarbor.Api.Services;

internal static class FileSystemStats
{
    public static DriveInfo GetDriveForPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var drive = DriveInfo.GetDrives()
            .Where(d => IsPathOnDrive(fullPath, d.Name))
            .OrderByDescending(d => Normalize(d.Name).Length)
            .FirstOrDefault();

        return drive ?? new DriveInfo(Path.GetPathRoot(fullPath) ?? fullPath);
    }

    private static bool IsPathOnDrive(string fullPath, string driveName)
    {
        var normalizedPath = Normalize(fullPath);
        var normalizedDrive = Normalize(driveName);

        return string.Equals(normalizedPath, normalizedDrive, StringComparison.Ordinal)
            || normalizedPath.StartsWith(
                normalizedDrive.EndsWith(Path.DirectorySeparatorChar)
                    ? normalizedDrive
                    : normalizedDrive + Path.DirectorySeparatorChar,
                StringComparison.Ordinal);
    }

    private static string Normalize(string path)
    {
        var normalized = Path.GetFullPath(path);
        return normalized.Length > 1 ? normalized.TrimEnd(Path.DirectorySeparatorChar) : normalized;
    }
}
