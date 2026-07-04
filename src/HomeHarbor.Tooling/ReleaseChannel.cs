namespace HomeHarbor.Tooling;

public static class ReleaseChannel
{
    public const string Dev = "dev";
    public const string Daily = "daily";
    public const string Stable = "stable";

    public static readonly IReadOnlyList<string> All = [Dev, Daily, Stable];

    public static bool IsValid(string? value)
        => NormalizeOrNull(value) is not null;

    public static string Require(string? value, string name = "channel")
        => NormalizeOrNull(value)
           ?? throw new InvalidOperationException($"{name} must be dev, daily, or stable, got: {Display(value)}");

    public static string Normalize(string? value, string name = "channel")
        => Require(value, name);

    private static string? NormalizeOrNull(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return null;
        }

        var lower = trimmed.ToLowerInvariant();
        return lower is Dev or Daily or Stable ? lower : null;
    }

    private static string Display(string? value)
        => string.IsNullOrWhiteSpace(value) ? "missing" : value.Trim();
}
