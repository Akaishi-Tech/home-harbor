using System.Globalization;

namespace HomeHarbor.Tooling;

public static class Env
{
    public static string String(string name, string fallback)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    public static string? Optional(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    public static int Int(string name, int fallback)
        => int.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;

    public static long Long(string name, long fallback)
        => long.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;

    public static bool Flag(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.Equals(value, "1", StringComparison.Ordinal) ||
            string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
    }
}
