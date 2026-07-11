using System.Globalization;
using HomeHarbor.Tooling;

internal static partial class AgentProgram
{
    private static async Task PrepareContainerAppRootAsync(
        ICommandRunner runner,
        string dataRoot,
        Guid containerId,
        string containerUser,
        string uidText,
        string gidText,
        CancellationToken cancellationToken)
    {
        if (!uint.TryParse(uidText, NumberStyles.None, CultureInfo.InvariantCulture, out var containerUid) ||
            !uint.TryParse(gidText, NumberStyles.None, CultureInfo.InvariantCulture, out var containerGid))
        {
            throw new InvalidOperationException("container runtime user and group ids must be numeric");
        }

        var subUidRanges = ReadSubIdRanges(
            Env.String("HOMEHARBOR_CONTAINER_SUBUID_PATH", "/etc/subuid"),
            containerUser,
            "subuid");
        var subGidRanges = ReadSubIdRanges(
            Env.String("HOMEHARBOR_CONTAINER_SUBGID_PATH", "/etc/subgid"),
            containerUser,
            "subgid");

        var appsRoot = Path.GetFullPath(Path.Combine(dataRoot, "apps"));
        _ = RootPathGuard.RequireNoSymlinkComponents(
            appsRoot,
            "container apps root",
            requireLeafDirectory: true);
        ValidateContainerAppsRootMetadata(await StatPathAsync(runner, appsRoot, cancellationToken));

        var appRoot = RootPathGuard.RequireChildPath(
            Path.Combine(appsRoot, containerId.ToString("N")),
            appsRoot,
            "container app data root");
        var created = !Directory.Exists(appRoot);
        if (created)
        {
            _ = RootPathGuard.CreateDirectory(appRoot, "container app data root");
            await ChownAsync(runner, appRoot, uidText, gidText, cancellationToken);
            await ChmodAsync(runner, appRoot, 0700, cancellationToken);
            return;
        }

        _ = RootPathGuard.RequireNoSymlinkComponents(
            appRoot,
            "container app data root",
            requireLeafDirectory: true);
        var metadata = await StatPathAsync(runner, appRoot, cancellationToken);

        if (!IsContainerNamespaceId(metadata.OwnerId, containerUid, subUidRanges))
        {
            var apiUidResult = await runner.RunAsync(
                "id",
                ["-u", "homeharbor"],
                cancellationToken: cancellationToken);
            var isLegacyApiOwner = apiUidResult.ExitCode == 0 &&
                uint.TryParse(apiUidResult.Stdout.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var apiUid) &&
                metadata.OwnerId == apiUid;
            var isInterruptedRootCreation = metadata.OwnerId == 0;
            if ((!isLegacyApiOwner && !isInterruptedRootCreation) ||
                Directory.EnumerateFileSystemEntries(appRoot).Any())
            {
                throw new InvalidOperationException(
                    "container app data root has unsafe ownership; refusing a recursive privileged ownership change: " + appRoot);
            }

            // Older releases let the API create this directory. Only an empty legacy
            // root is migrated: recursively chowning API-controlled content as root
            // would turn hard links into a privileged ownership-change primitive.
            await ChownAsync(runner, appRoot, uidText, gidText, cancellationToken);
            await ChmodAsync(runner, appRoot, 0700, cancellationToken);
            return;
        }

        ValidateContainerAppRootOwnership(
            metadata,
            containerUid,
            containerGid,
            subUidRanges,
            subGidRanges);
        if (metadata.Mode != 0700)
        {
            await ChmodAsync(runner, appRoot, 0700, cancellationToken);
        }
    }

    internal static void ValidateContainerAppsRootMetadata(ContainerPathMetadata metadata)
    {
        if (metadata.OwnerId != 0 || metadata.GroupId != 0 || metadata.Mode != 0711)
        {
            throw new InvalidOperationException(
                "container apps root must be root-owned with mode 0711");
        }
    }

    internal static void ValidateContainerAppRootOwnership(
        ContainerPathMetadata metadata,
        uint containerUid,
        uint containerGid,
        IReadOnlyList<SubIdRange> subUidRanges,
        IReadOnlyList<SubIdRange> subGidRanges)
    {
        if (!IsContainerNamespaceId(metadata.OwnerId, containerUid, subUidRanges) ||
            !IsContainerNamespaceId(metadata.GroupId, containerGid, subGidRanges))
        {
            throw new InvalidOperationException(
                "container app data root must be owned by the rootless container user namespace");
        }
    }

    internal static IReadOnlyList<SubIdRange> ParseSubIdRanges(
        string content,
        string user,
        string label)
    {
        var ranges = new List<SubIdRange>();
        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var parts = line.Split(':');
            if (parts.Length == 0 || !string.Equals(parts[0], user, StringComparison.Ordinal))
            {
                continue;
            }
            if (parts.Length != 3 ||
                !uint.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var start) ||
                !uint.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var count) ||
                count == 0 || (ulong)start + count > (ulong)uint.MaxValue + 1)
            {
                throw new InvalidOperationException("invalid " + label + " mapping for " + user);
            }

            var candidate = new SubIdRange(start, count);
            if (ranges.Any(existing => existing.Overlaps(candidate)))
            {
                throw new InvalidOperationException("overlapping " + label + " mappings for " + user);
            }
            ranges.Add(candidate);
        }

        if (ranges.Count == 0)
        {
            throw new InvalidOperationException("missing " + label + " mapping for " + user);
        }
        return ranges;
    }

    private static IReadOnlyList<SubIdRange> ReadSubIdRanges(string path, string user, string label)
    {
        try
        {
            return ParseSubIdRanges(File.ReadAllText(path), user, label);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException("failed to read " + label + " mappings: " + path, ex);
        }
    }

    private static bool IsContainerNamespaceId(
        uint value,
        uint runtimeId,
        IReadOnlyList<SubIdRange> ranges)
        => value == runtimeId || ranges.Any(range => range.Contains(value));

    private static async Task<ContainerPathMetadata> StatPathAsync(
        ICommandRunner runner,
        string path,
        CancellationToken cancellationToken)
    {
        var result = (await runner.RunAsync(
            "stat",
            ["--format=%u:%g:%a", "--", path],
            cancellationToken: cancellationToken))
            .EnsureSuccess("failed to inspect container data path");
        return ParseContainerPathMetadata(result.Stdout, path);
    }

    internal static ContainerPathMetadata ParseContainerPathMetadata(string output, string path)
    {
        var fields = output.Trim().Split(':');
        if (fields.Length != 3 ||
            !uint.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ownerId) ||
            !uint.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var groupId))
        {
            throw new InvalidOperationException("stat returned invalid container data ownership: " + path);
        }

        // Agent mode arguments intentionally use octal-looking decimal digits
        // (for example 0700 is formatted as "0700" for chmod), so preserve the
        // stat representation instead of converting it to a base-10 bitmask.
        if (fields[2].Length is < 3 or > 4 ||
            fields[2].Any(character => character is < '0' or > '7') ||
            !int.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var mode) ||
            mode > 07777)
        {
            throw new InvalidOperationException("stat returned invalid container data mode: " + path);
        }
        return new ContainerPathMetadata(ownerId, groupId, mode);
    }

    internal readonly record struct ContainerPathMetadata(uint OwnerId, uint GroupId, int Mode);

    internal readonly record struct SubIdRange(uint Start, uint Count)
    {
        internal bool Contains(uint value)
            => value >= Start && (ulong)value < (ulong)Start + Count;

        internal bool Overlaps(SubIdRange other)
            => (ulong)Start < (ulong)other.Start + other.Count &&
               (ulong)other.Start < (ulong)Start + Count;
    }
}
