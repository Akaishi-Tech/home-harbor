using System.Globalization;
using System.Text.RegularExpressions;

namespace HomeHarbor.Tooling;

public sealed record SuperMapExtent(long StartSector, long SectorCount, string Mode, string Device, long PhysicalSector)
{
    public string ToTableLine()
        => string.Create(CultureInfo.InvariantCulture, $"{StartSector} {SectorCount} {Mode} {Device} {PhysicalSector}");
}

public static partial class SuperMapParser
{
    public static IReadOnlyList<SuperMapExtent> ParseLpdump(string lpdumpOutput, string superDevice, string logicalPartition)
    {
        var found = false;
        var inPartition = false;
        long nextSector = 0;
        var extents = new List<SuperMapExtent>();

        foreach (var rawLine in lpdumpOutput.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.StartsWith("  Name: ", StringComparison.Ordinal))
            {
                var name = line["  Name: ".Length..];
                inPartition = string.Equals(name, logicalPartition, StringComparison.Ordinal);
                if (inPartition)
                {
                    found = true;
                    nextSector = 0;
                    extents.Clear();
                }

                continue;
            }

            if (inPartition && line.StartsWith("------------------------", StringComparison.Ordinal))
            {
                inPartition = false;
                continue;
            }

            if (!inPartition)
            {
                continue;
            }

            var match = ExtentRegex().Match(line);
            if (!match.Success)
            {
                continue;
            }

            var start = long.Parse(match.Groups["start"].Value, CultureInfo.InvariantCulture);
            var end = long.Parse(match.Groups["end"].Value, CultureInfo.InvariantCulture);
            var physical = long.Parse(match.Groups["physical"].Value, CultureInfo.InvariantCulture);
            if (end < start)
            {
                throw new InvalidOperationException($"extent ends before it starts for {logicalPartition}: {line.Trim()}");
            }

            if (extents.Count == 0 && start != 0)
            {
                throw new InvalidOperationException($"first extent for {logicalPartition} does not start at sector 0");
            }

            if (extents.Count > 0 && start != nextSector)
            {
                throw new InvalidOperationException($"extents for {logicalPartition} are not contiguous");
            }

            var count = end - start + 1;
            extents.Add(new SuperMapExtent(start, count, "linear", superDevice, physical));
            nextSector = end + 1;
        }

        return !found
            ? throw new InvalidOperationException("logical partition not found: " + logicalPartition)
            : extents.Count == 0
            ? throw new InvalidOperationException("logical partition has no linear extents: " + logicalPartition)
            : (IReadOnlyList<SuperMapExtent>)extents;
    }

    public static string ToTable(IReadOnlyList<SuperMapExtent> extents)
        => string.Join('\n', extents.Select(e => e.ToTableLine())) + "\n";

    [GeneratedRegex(@"^\s*(?<start>[0-9]+)\s+\.\.\s+(?<end>[0-9]+)\s+linear\s+\S+\s+(?<physical>[0-9]+)\b", RegexOptions.CultureInvariant)]
    private static partial Regex ExtentRegex();
}

public sealed class SuperMapper(ICommandRunner? runner = null)
{
    private readonly ICommandRunner _runner = runner ?? new ProcessCommandRunner();

    public async Task<string> TableAsync(string superDevice, string logicalPartition, CancellationToken cancellationToken = default)
    {
        var result = await _runner.RunAsync("lpdump", [superDevice], cancellationToken: cancellationToken);
        _ = result.EnsureSuccess("lpdump failed");
        return SuperMapParser.ToTable(SuperMapParser.ParseLpdump(result.Stdout, superDevice, logicalPartition));
    }

    public async Task CreateAsync(
        string mapperName,
        string superDevice,
        string logicalPartition,
        string accessMode = "rw",
        CancellationToken cancellationToken = default)
    {
        if (accessMode is not ("ro" or "rw"))
        {
            throw new ArgumentException("access mode must be ro or rw: " + accessMode, nameof(accessMode));
        }

        await RemoveAsync(mapperName, cancellationToken);
        var table = await TableAsync(superDevice, logicalPartition, cancellationToken);
        var args = accessMode == "ro"
            ? new[] { "-r", "create", mapperName }
            : ["create", mapperName];
        var result = await _runner.RunAsync("dmsetup", args, new CommandRunOptions(StandardInput: table), cancellationToken);
        if (result.ExitCode != 0)
        {
            await RemoveAsync(mapperName, cancellationToken);
            result = await _runner.RunAsync("dmsetup", args, new CommandRunOptions(StandardInput: table), cancellationToken);
            _ = result.EnsureSuccess("dmsetup create failed");
        }

        var mapperPath = "/dev/mapper/" + mapperName;
        for (var i = 0; i < 10; i++)
        {
            if (File.Exists(mapperPath))
            {
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }

        throw new InvalidOperationException("mapper device did not appear: " + mapperPath);
    }

    public async Task RemoveAsync(string mapperName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(mapperName))
        {
            return;
        }

        var deferred = await _runner.RunAsync("dmsetup", ["remove", "--deferred", mapperName], cancellationToken: cancellationToken);
        if (deferred.ExitCode == 0)
        {
            return;
        }

        _ = await _runner.RunAsync("dmsetup", ["remove", mapperName], cancellationToken: cancellationToken);
    }
}
