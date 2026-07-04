namespace HomeHarbor.Tooling;

public static class KernelChannel
{
    public const string Generic = "generic";
    public const string Zfs = "zfs";

    public static readonly IReadOnlyList<string> All = [Generic, Zfs];

    public static bool IsValid(string? value)
        => NormalizeOrNull(value) is not null;

    public static string Require(string? value, string name = "kernel channel")
        => NormalizeOrNull(value)
           ?? throw new InvalidOperationException($"{name} must be generic or zfs, got: {Display(value)}");

    public static string Normalize(string? value, string name = "kernel channel")
        => Require(value, name);

    private static string? NormalizeOrNull(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return null;
        }

        var lower = trimmed.ToLowerInvariant();
        return lower is Generic or Zfs ? lower : null;
    }

    private static string Display(string? value)
        => string.IsNullOrWhiteSpace(value) ? "missing" : value.Trim();
}
