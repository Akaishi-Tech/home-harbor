namespace HomeHarbor.Tooling;

public static class TarSafety
{
    public static string ValidateSingleTopLevelDirectory(IEnumerable<string> members, string label, params string[] allowedPrefixes)
    {
        string? top = null;
        foreach (var member in members.Where(m => !string.IsNullOrWhiteSpace(m)))
        {
            ValidateMemberPath(member, label);
            var normalized = member.TrimEnd('/');
            var memberTop = normalized.Split('/', 2)[0];
            if (top is null)
            {
                top = memberTop;
            }
            else if (!string.Equals(top, memberTop, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"{label} must contain exactly one top-level directory.");
            }
        }

        return string.IsNullOrWhiteSpace(top)
            ? throw new InvalidOperationException($"{label} is empty.")
            : allowedPrefixes.Length > 0 && !allowedPrefixes.Any(prefix => top.StartsWith(prefix, StringComparison.Ordinal))
            ? throw new InvalidOperationException(
                $"{label} top-level directory must start with one of: {string.Join(", ", allowedPrefixes)}.")
            : top;
    }

    public static void ValidateMemberPath(string member, string label)
    {
        var normalized = member.TrimEnd('/');
        if (normalized.Length == 0 ||
            normalized.StartsWith('/') ||
            normalized.Contains('\\'))
        {
            throw new InvalidOperationException($"unsafe {label} member path: {member}");
        }

        foreach (var component in normalized.Split('/'))
        {
            if (component.Length == 0 || component is "." or "..")
            {
                throw new InvalidOperationException($"unsafe {label} member path: {member}");
            }
        }
    }
}
