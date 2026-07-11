using System.Text.Json;

namespace HomeHarbor.Tooling;

public static class BlockDeviceSafety
{
    private const int MaxDeviceTreeNodes = 4096;

    public static async Task<IReadOnlySet<string>> RootAncestorDevicesAsync(
        ICommandRunner runner,
        CancellationToken cancellationToken)
        => await RootAncestorDevicesAsync(runner, allowVerifiedArchisoRoot: false, cancellationToken);

    public static async Task<IReadOnlySet<string>> RootAncestorDevicesAsync(
        ICommandRunner runner,
        bool allowVerifiedArchisoRoot,
        CancellationToken cancellationToken)
    {
        var sourceResult = await runner.RunAsync(
            "findmnt",
            ["-n", "-o", "SOURCE", "/"],
            cancellationToken: cancellationToken);
        if (sourceResult.ExitCode != 0)
        {
            throw new InvalidOperationException("failed to identify the root filesystem device: " + sourceResult.Command);
        }

        var sourceLines = NonEmptyLines(sourceResult.Stdout);
        if (sourceLines.Count != 1)
        {
            throw new InvalidOperationException("findmnt returned an invalid root filesystem device");
        }

        if (IsDevicePath(sourceLines[0]))
        {
            return await DeviceAncestorsAsync(runner, sourceLines[0], "root filesystem device", cancellationToken);
        }

        if (!allowVerifiedArchisoRoot || !string.Equals(sourceLines[0], "airootfs", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("findmnt returned an invalid root filesystem device");
        }

        return await VerifiedArchisoAncestorDevicesAsync(runner, cancellationToken);
    }

    private static async Task<IReadOnlySet<string>> VerifiedArchisoAncestorDevicesAsync(
        ICommandRunner runner,
        CancellationToken cancellationToken)
    {
        var rootType = await SingleProbeLineAsync(
            runner,
            "findmnt",
            ["-n", "-o", "FSTYPE", "/"],
            "ArchISO root filesystem type",
            cancellationToken);
        if (!string.Equals(rootType, "overlay", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("unverified ArchISO root filesystem type");
        }

        var (bootSource, bootType) = await MountSourceAndTypeAsync(
            runner,
            "/run/archiso/bootmnt",
            "ArchISO boot media",
            cancellationToken);
        if (!IsDevicePath(bootSource) || !string.Equals(bootType, "iso9660", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("unverified ArchISO boot media mount");
        }

        var (imageSource, imageType) = await MountSourceAndTypeAsync(
            runner,
            "/run/archiso/airootfs",
            "ArchISO root image",
            cancellationToken);
        if (!imageSource.StartsWith("/dev/loop", StringComparison.Ordinal) ||
            !IsDevicePath(imageSource) ||
            !string.Equals(imageType, "erofs", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("unverified ArchISO root image mount");
        }

        var resolvedImageSource = await ResolveDevicePathAsync(
            runner,
            imageSource,
            "ArchISO root image device",
            cancellationToken);
        var backingFile = await SingleProbeLineAsync(
            runner,
            "losetup",
            ["--noheadings", "--output", "BACK-FILE", resolvedImageSource],
            "ArchISO root image backing file",
            cancellationToken);
        var bootMount = "/run/archiso/bootmnt";
        string fullBackingFile;
        try
        {
            fullBackingFile = Path.GetFullPath(backingFile);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidOperationException("ArchISO root image backing file path is invalid", ex);
        }
        if (!Path.IsPathFullyQualified(backingFile) ||
            !SecurityGuards.IsInsideDirectory(fullBackingFile, bootMount) ||
            string.Equals(fullBackingFile, bootMount, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("ArchISO root image is not backed by the verified boot media");
        }

        var ancestors = new HashSet<string>(StringComparer.Ordinal);
        ancestors.UnionWith(await DeviceAncestorsAsync(runner, bootSource, "ArchISO boot media device", cancellationToken));
        ancestors.UnionWith(await DeviceAncestorsAsync(runner, resolvedImageSource, "ArchISO root image device", cancellationToken));
        return ancestors;
    }

    private static async Task<(string Source, string FileSystemType)> MountSourceAndTypeAsync(
        ICommandRunner runner,
        string mountpoint,
        string label,
        CancellationToken cancellationToken)
    {
        var line = await SingleProbeLineAsync(
            runner,
            "findmnt",
            ["-n", "-o", "SOURCE,FSTYPE", mountpoint],
            label,
            cancellationToken);
        var fields = line.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (fields.Length != 2)
        {
            throw new InvalidOperationException("findmnt returned invalid " + label + " details");
        }
        return (fields[0], fields[1]);
    }

    private static async Task<string> SingleProbeLineAsync(
        ICommandRunner runner,
        string fileName,
        IReadOnlyList<string> arguments,
        string label,
        CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(fileName, arguments, cancellationToken: cancellationToken);
        var lines = NonEmptyLines(result.Stdout);
        if (result.ExitCode != 0 || lines.Count != 1)
        {
            throw new InvalidOperationException("failed to verify " + label + ": " + result.Command);
        }
        return lines[0];
    }

    private static async Task<IReadOnlySet<string>> DeviceAncestorsAsync(
        ICommandRunner runner,
        string source,
        string label,
        CancellationToken cancellationToken)
    {
        var rootSource = await ResolveDevicePathAsync(runner, source, label, cancellationToken);
        var ancestorsResult = await runner.RunAsync(
            "lsblk",
            ["-nrpo", "PATH", "-s", rootSource],
            cancellationToken: cancellationToken);
        if (ancestorsResult.ExitCode != 0)
        {
            throw new InvalidOperationException("failed to enumerate root filesystem device ancestors: " + ancestorsResult.Command);
        }

        var ancestorLines = NonEmptyLines(ancestorsResult.Stdout);
        if (ancestorLines.Count is < 1 or > MaxDeviceTreeNodes || ancestorLines.Any(path => !IsDevicePath(path)))
        {
            throw new InvalidOperationException("lsblk returned an invalid root filesystem device ancestor chain");
        }

        var ancestors = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in ancestorLines)
        {
            _ = ancestors.Add(await ResolveDevicePathAsync(runner, path, "root filesystem ancestor", cancellationToken));
        }
        if (!ancestors.Contains(rootSource))
        {
            throw new InvalidOperationException(label + " ancestor chain omitted the source device");
        }

        return ancestors;
    }

    public static async Task<bool> DeviceHasMountsAsync(
        ICommandRunner runner,
        string path,
        CancellationToken cancellationToken)
    {
        var root = await ReadDeviceTreeAsync(runner, path, "PATH,MOUNTPOINTS", "mount state", cancellationToken);
        foreach (var node in EnumerateDeviceTree(root, path))
        {
            if (!node.TryGetProperty("mountpoints", out var mountpoints))
            {
                throw new InvalidOperationException("lsblk mount-state response omitted mountpoints");
            }
            if (mountpoints.ValueKind == JsonValueKind.Null)
            {
                continue;
            }
            if (mountpoints.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException("lsblk mount-state response contained invalid mountpoints");
            }
            foreach (var mountpoint in mountpoints.EnumerateArray())
            {
                if (mountpoint.ValueKind == JsonValueKind.Null)
                {
                    continue;
                }
                if (mountpoint.ValueKind != JsonValueKind.String)
                {
                    throw new InvalidOperationException("lsblk mount-state response contained a non-string mountpoint");
                }
                if (!string.IsNullOrWhiteSpace(mountpoint.GetString()))
                {
                    return true;
                }
            }
        }

        return false;
    }

    public static async Task<IReadOnlyList<string>> DeviceLabelsAsync(
        ICommandRunner runner,
        string path,
        CancellationToken cancellationToken)
    {
        var root = await ReadDeviceTreeAsync(runner, path, "PATH,LABEL,PARTLABEL", "label state", cancellationToken);
        var labels = new List<string>();
        foreach (var node in EnumerateDeviceTree(root, path))
        {
            AddOptionalString(node, "label", labels);
            AddOptionalString(node, "partlabel", labels);
        }
        return labels;
    }

    private static async Task<JsonElement> ReadDeviceTreeAsync(
        ICommandRunner runner,
        string path,
        string columns,
        string probeName,
        CancellationToken cancellationToken)
    {
        if (!IsDevicePath(path))
        {
            throw new InvalidOperationException("device safety probe requires a canonical /dev path: " + path);
        }

        var result = await runner.RunAsync(
            "lsblk",
            ["--json", "--tree", "--output", columns, path],
            cancellationToken: cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"failed to read device {probeName}: {result.Command}");
        }

        try
        {
            using var document = JsonDocument.Parse(result.Stdout);
            if (!document.RootElement.TryGetProperty("blockdevices", out var devices) ||
                devices.ValueKind != JsonValueKind.Array ||
                devices.GetArrayLength() != 1)
            {
                throw new InvalidOperationException($"lsblk did not return exactly one device for the {probeName} probe");
            }

            var root = devices[0];
            if (!root.TryGetProperty("path", out var rootPath) ||
                rootPath.ValueKind != JsonValueKind.String ||
                !string.Equals(rootPath.GetString(), path, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"lsblk {probeName} response did not match the requested device");
            }
            return root.Clone();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"lsblk returned invalid JSON for the device {probeName} probe", ex);
        }
    }

    private static IEnumerable<JsonElement> EnumerateDeviceTree(JsonElement root, string requestedPath)
    {
        var pending = new Stack<JsonElement>();
        pending.Push(root);
        var count = 0;
        while (pending.Count > 0)
        {
            var node = pending.Pop();
            count++;
            if (count > MaxDeviceTreeNodes)
            {
                throw new InvalidOperationException("lsblk device tree exceeded the safety limit");
            }
            if (node.ValueKind != JsonValueKind.Object ||
                !node.TryGetProperty("path", out var path) ||
                path.ValueKind != JsonValueKind.String ||
                !IsDevicePath(path.GetString() ?? string.Empty))
            {
                throw new InvalidOperationException("lsblk device tree contained an invalid device path for " + requestedPath);
            }

            yield return node;
            if (!node.TryGetProperty("children", out var children) || children.ValueKind == JsonValueKind.Null)
            {
                continue;
            }
            if (children.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException("lsblk device tree contained invalid children for " + requestedPath);
            }
            foreach (var child in children.EnumerateArray())
            {
                pending.Push(child);
            }
        }
    }

    private static void AddOptionalString(JsonElement node, string property, ICollection<string> output)
    {
        if (!node.TryGetProperty(property, out var value))
        {
            throw new InvalidOperationException("lsblk label-state response omitted " + property);
        }
        if (value.ValueKind == JsonValueKind.Null)
        {
            return;
        }
        if (value.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException("lsblk label-state response contained an invalid " + property);
        }

        var text = value.GetString();
        if (!string.IsNullOrEmpty(text))
        {
            output.Add(text);
        }
    }

    private static async Task<string> ResolveDevicePathAsync(
        ICommandRunner runner,
        string path,
        string label,
        CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync("readlink", ["-f", path], cancellationToken: cancellationToken);
        var lines = NonEmptyLines(result.Stdout);
        if (result.ExitCode != 0 || lines.Count != 1 || !IsDevicePath(lines[0]))
        {
            throw new InvalidOperationException("failed to resolve " + label + ": " + path);
        }
        return lines[0];
    }

    private static List<string> NonEmptyLines(string value)
        => [.. value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    private static bool IsDevicePath(string path)
        => path.StartsWith("/dev/", StringComparison.Ordinal) &&
           path.Length > "/dev/".Length &&
           !path.Any(char.IsWhiteSpace) &&
           !path.Any(char.IsControl);
}
