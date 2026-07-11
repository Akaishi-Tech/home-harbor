using System.Globalization;

namespace HomeHarbor.Tooling;

public static class ReleaseSequence
{
    public const string EnvironmentVariable = "HOMEHARBOR_RELEASE_SEQUENCE";
    public const string OsReleaseKey = "HOMEHARBOR_RELEASE_SEQUENCE";
    public const string KernelArgument = "homeharbor.release_sequence";

    public static long RequireEnvironment()
        => RequirePositive(Environment.GetEnvironmentVariable(EnvironmentVariable), EnvironmentVariable);

    public static long RequirePositive(string? value, string label)
        => long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : throw new InvalidOperationException(label + " must be a positive decimal integer.");

    public static long RequirePositive(long value, string label)
        => value > 0 ? value : throw new InvalidOperationException(label + " must be positive.");

    public static void RequireUpgrade(long target, long current)
    {
        RequirePositive(target, "target releaseSequence");
        RequirePositive(current, "current releaseSequence");
        if (target <= current)
        {
            throw new InvalidOperationException(
                $"OTA releaseSequence {target} must be newer than the trusted current floor {current}.");
        }
    }

    public static void RequireComponentUpgrade(long target, long currentComponent, long otherComponent)
    {
        RequirePositive(target, "target releaseSequence");
        if (currentComponent < 0 || otherComponent < 0)
        {
            throw new InvalidOperationException("trusted current releaseSequence anchors cannot be negative.");
        }

        if (target <= currentComponent)
        {
            throw new InvalidOperationException(
                $"OTA releaseSequence {target} must advance the current component sequence {currentComponent}.");
        }

        if (target < otherComponent)
        {
            throw new InvalidOperationException(
                $"OTA releaseSequence {target} cannot be older than the other trusted component sequence {otherComponent}.");
        }
    }

    public static long ReadOsRelease(string path)
        => ReadOptionalOsRelease(path)
           ?? throw new InvalidOperationException($"verified os-release must contain exactly one {OsReleaseKey} value.");

    public static long? ReadOptionalOsRelease(string path)
        => ReadOptionalUniqueKeyValue(path, OsReleaseKey, "verified os-release");

    public static long ReadKernelCommandLine(string path)
        => ReadOptionalKernelCommandLine(path)
           ?? throw new InvalidOperationException(
               $"signed kernel command line must contain exactly one {KernelArgument} argument.");

    public static long? ReadOptionalKernelCommandLine(string path)
    {
        var content = File.ReadAllText(path);
        var values = content
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Where(token => token.StartsWith(KernelArgument + "=", StringComparison.Ordinal))
            .Select(token => token[(KernelArgument.Length + 1)..])
            .ToArray();
        if (values.Length == 0)
        {
            return null;
        }

        if (values.Length != 1)
        {
            throw new InvalidOperationException(
                $"signed kernel command line must contain exactly one {KernelArgument} argument.");
        }

        return RequirePositive(values[0], "signed kernel release sequence");
    }

    public static async Task StampRootfsOsReleaseAsync(
        string rootfs,
        long sequence,
        CancellationToken cancellationToken = default)
    {
        RequirePositive(sequence, "release sequence");
        var path = Path.Combine(Path.GetFullPath(rootfs), "usr", "lib", "os-release");
        if (!File.Exists(path) || new FileInfo(path).LinkTarget is not null)
        {
            throw new InvalidOperationException("rootfs /usr/lib/os-release must be a regular file before stamping releaseSequence.");
        }

        var lines = (await File.ReadAllLinesAsync(path, cancellationToken))
            .Where(line => !line.StartsWith(OsReleaseKey + "=", StringComparison.Ordinal))
            .ToList();
        lines.Add(OsReleaseKey + "=" + sequence.ToString(CultureInfo.InvariantCulture));
        await FileWrites.AtomicWriteTextAsync(path, string.Join('\n', lines) + "\n", 0644, cancellationToken);
    }

    private static long? ReadOptionalUniqueKeyValue(string path, string key, string label)
    {
        var values = File.ReadLines(path)
            .Where(line => line.StartsWith(key + "=", StringComparison.Ordinal))
            .Select(line => Unquote(line[(key.Length + 1)..].Trim()))
            .ToArray();
        if (values.Length == 0)
        {
            return null;
        }

        if (values.Length != 1)
        {
            throw new InvalidOperationException($"{label} must contain exactly one {key} value.");
        }

        return RequirePositive(values[0], label + " release sequence");
    }

    private static string Unquote(string value)
        => value.Length >= 2 && value[0] == '"' && value[^1] == '"'
            ? value[1..^1]
            : value;
}
