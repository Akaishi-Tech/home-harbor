using System.Text.RegularExpressions;

namespace HomeHarbor.Tooling;

public static partial class SecurityGuards
{
    public static bool IsSafeVersion(string value)
        => SafeVersionRegex().IsMatch(value);

    public static bool IsSafeReleaseKeyId(string keyId)
        => SafeReleaseKeyIdRegex().IsMatch(keyId) && keyId != "dev" && !keyId.StartsWith("test-", StringComparison.Ordinal);

    public static bool IsSafeSshHost(string host)
        => !string.IsNullOrWhiteSpace(host) &&
            !host.StartsWith('-') &&
            !host.Any(char.IsWhiteSpace) &&
            !host.Contains('\'') &&
            !host.Contains('"') &&
            SafeSshHostRegex().IsMatch(host);

    public static bool IsSafeAbsoluteRemotePath(string path)
    {
        return !string.IsNullOrWhiteSpace(path) &&
            path.StartsWith('/') &&
!path.Contains('\'') &&
!path.Any(char.IsWhiteSpace) &&
!path.Contains('\\') &&
!path.Contains("//", StringComparison.Ordinal) && path.Split('/', StringSplitOptions.RemoveEmptyEntries).All(component => component is not "." and not "..");
    }

    public static bool IsInsideDirectory(string candidate, string root)
    {
        var candidateFull = Path.GetFullPath(candidate);
        var rootFull = Path.GetFullPath(root);
        return string.Equals(candidateFull, rootFull, StringComparison.Ordinal) ||
            candidateFull.StartsWith(rootFull.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    [GeneratedRegex("^[A-Za-z0-9._-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeVersionRegex();

    [GeneratedRegex("^[A-Za-z0-9._-]{4,128}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeReleaseKeyIdRegex();

    [GeneratedRegex(@"^([A-Za-z0-9_.-]+@)?(\[[A-Fa-f0-9:.]+\]|[A-Za-z0-9_.][A-Za-z0-9_.-]*)$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeSshHostRegex();
}
