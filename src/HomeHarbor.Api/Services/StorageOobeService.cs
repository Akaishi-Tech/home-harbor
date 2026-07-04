using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using HomeHarbor.Tooling;
using Microsoft.Extensions.Options;

namespace HomeHarbor.Api.Services;

public sealed class StorageOobeService(
    IOptions<StorageOobeOptions> options,
    ILogger<StorageOobeService> logger,
    KernelModuleDetector kernelModules) : IStorageOobeService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly StorageOobeOptions _options = options.Value;

    public async Task<StorageInventory> InventoryAsync(CancellationToken cancellationToken)
    {
        var warnings = new List<string>();
        var mounts = await ReadMountsAsync(warnings, cancellationToken);
        var rootParent = await RootParentDeviceAsync(cancellationToken);
        var devices = await ReadLsblkAsync(rootParent, warnings, cancellationToken);
        var protectedDevices = devices
            .Where(d => d.IsProtected || d.IsSystem)
            .Select(d => d.Path)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();
        var targets = BuildTargets(devices);
        var fileSystems = await DetectFileSystemsAsync(cancellationToken);

        return new StorageInventory(devices, targets, mounts, protectedDevices, warnings, fileSystems);
    }

    public StorageRecommendation Recommend(StorageInventory inventory, StorageUseProfile profile)
        => StorageOobePlanner.Recommend(inventory, profile);

    public async Task<StoragePlan> CreatePlanAsync(
        StorageInventory inventory,
        StoragePlanRequest request,
        CancellationToken cancellationToken)
    {
        var plan = StorageOobePlanner.CreatePlan(inventory, request);
        var directory = Path.Combine(_options.StateDirectory, "plans");
        _ = Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, plan.PlanId + ".json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(plan, JsonOptions), cancellationToken);
        return plan;
    }

    public async Task<StorageApplyStatus> ApplyAsync(
        string planId,
        string confirmation,
        string? recoveryPassphrase,
        CancellationToken cancellationToken)
    {
        var plan = await ReadPlanAsync(planId, cancellationToken);
        if (!string.Equals(confirmation, plan.ConfirmPhrase, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Confirmation phrase did not match the storage plan.");
        }
        if (string.IsNullOrEmpty(recoveryPassphrase))
        {
            throw new InvalidOperationException("Recovery passphrase is required to apply the storage plan.");
        }

        _ = Directory.CreateDirectory(_options.StateDirectory);
        var pendingPath = PendingPlanPath();
        var status = new StorageApplyStatus(
            StorageApplyState.PendingReboot,
            Progress: 5,
            Message: "Storage plan is queued and will be applied by the root service.",
            Error: null,
            PlanId: plan.PlanId,
            UpdatedAt: DateTimeOffset.UtcNow);

        await File.WriteAllTextAsync(pendingPath, JsonSerializer.Serialize(plan, JsonOptions), cancellationToken);
        await WriteOneShotPassphraseAsync(recoveryPassphrase, cancellationToken);
        await TouchRequestAsync(cancellationToken);
        await WriteStatusAsync(status, cancellationToken);
        return status;
    }

    public async Task<StorageApplyStatus> StatusAsync(CancellationToken cancellationToken)
    {
        var statusPath = StatusPath();
        if (File.Exists(statusPath))
        {
            try
            {
                var status = await JsonSerializer.DeserializeAsync<StorageApplyStatus>(
                    File.OpenRead(statusPath),
                    JsonOptions,
                    cancellationToken);
                if (status is not null) return status;
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                logger.LogWarning(ex, "Failed to read storage OOBE status.");
            }
        }

        return File.Exists(PendingPlanPath())
            ? new StorageApplyStatus(
                StorageApplyState.PendingReboot,
                Progress: 5,
                Message: "Storage plan is queued.",
                Error: null,
                PlanId: null,
                UpdatedAt: DateTimeOffset.UtcNow)
            : new StorageApplyStatus(
            StorageApplyState.Idle,
            Progress: 0,
            Message: "No storage plan has been applied.",
            Error: null,
            PlanId: null,
            UpdatedAt: DateTimeOffset.UtcNow);
    }

    public async Task<bool> IsReadyAsync(CancellationToken cancellationToken)
        => (await StatusAsync(cancellationToken)).State == StorageApplyState.Succeeded;

    private async Task<StoragePlan> ReadPlanAsync(string planId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(planId) || planId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new InvalidOperationException("Invalid storage plan id.");
        }

        var path = Path.Combine(_options.StateDirectory, "plans", planId + ".json");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Storage plan was not found.", path);
        }

        await using var input = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<StoragePlan>(input, JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("Storage plan could not be read.");
    }

    private async Task WriteStatusAsync(StorageApplyStatus status, CancellationToken cancellationToken)
    {
        _ = Directory.CreateDirectory(_options.StateDirectory);
        await File.WriteAllTextAsync(StatusPath(), JsonSerializer.Serialize(status, JsonOptions), cancellationToken);
    }

    private async Task WriteOneShotPassphraseAsync(string passphrase, CancellationToken cancellationToken)
    {
        var path = _options.OneShotPassphrasePath;
        _ = Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        await File.WriteAllTextAsync(path, passphrase, cancellationToken);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private async Task TouchRequestAsync(CancellationToken cancellationToken)
    {
        var path = _options.RequestPath;
        _ = Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        await File.WriteAllTextAsync(path, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture), cancellationToken);
    }

    private string PendingPlanPath() => Path.Combine(_options.StateDirectory, "pending-plan.json");

    private string StatusPath() => Path.Combine(_options.StateDirectory, "status.json");

    private async Task<IReadOnlyList<StorageMount>> ReadMountsAsync(List<string> warnings, CancellationToken cancellationToken)
    {
        var (ExitCode, Output) = await ProcessRunner.CaptureAsync(
            "findmnt",
            ["-J", "-b", "-o", "TARGET,SOURCE,FSTYPE,OPTIONS"],
            TimeSpan.FromSeconds(4),
            cancellationToken);
        if (ExitCode != 0)
        {
            warnings.Add("findmnt was unavailable; mount information may be incomplete.");
            return [];
        }

        try
        {
            using var doc = JsonDocument.Parse(Output);
            if (!doc.RootElement.TryGetProperty("filesystems", out var filesystems)) return [];
            var mounts = new List<StorageMount>();
            foreach (var node in filesystems.EnumerateArray())
            {
                ReadMountNode(node, mounts);
            }

            return mounts;
        }
        catch (JsonException ex)
        {
            warnings.Add("findmnt returned invalid JSON.");
            logger.LogWarning(ex, "Failed to parse findmnt output.");
            return [];
        }
    }

    private static void ReadMountNode(JsonElement node, List<StorageMount> mounts)
    {
        mounts.Add(new StorageMount(
            StringProperty(node, "target"),
            StringProperty(node, "source"),
            StringProperty(node, "fstype"),
            StringProperty(node, "options")));

        if (!node.TryGetProperty("children", out var children)) return;
        foreach (var child in children.EnumerateArray())
        {
            ReadMountNode(child, mounts);
        }
    }

    private async Task<IReadOnlyList<StorageDevice>> ReadLsblkAsync(
        string? rootParent,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        var (ExitCode, Output) = await ProcessRunner.CaptureAsync(
            "lsblk",
            ["-J", "-b", "-o", "NAME,PATH,SIZE,TYPE,MODEL,SERIAL,TRAN,ROTA,RM,MOUNTPOINTS,FSTYPE,LABEL,PARTLABEL,UUID,PKNAME"],
            TimeSpan.FromSeconds(5),
            cancellationToken);
        if (ExitCode != 0)
        {
            warnings.Add("lsblk was unavailable; storage inventory is empty.");
            return [];
        }

        try
        {
            using var doc = JsonDocument.Parse(Output);
            var devices = new List<StorageDevice>();
            foreach (var device in doc.RootElement.GetProperty("blockdevices").EnumerateArray())
            {
                devices.Add(await ReadDeviceAsync(device, rootParent, warnings, cancellationToken));
            }

            return devices;
        }
        catch (JsonException ex)
        {
            warnings.Add("lsblk returned invalid JSON.");
            logger.LogWarning(ex, "Failed to parse lsblk output.");
            return [];
        }
    }

    private async Task<StorageDevice> ReadDeviceAsync(
        JsonElement node,
        string? rootParent,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        var children = new List<StorageDevice>();
        if (node.TryGetProperty("children", out var childElements))
        {
            foreach (var child in childElements.EnumerateArray())
            {
                children.Add(await ReadDeviceAsync(child, rootParent, warnings, cancellationToken));
            }
        }

        var path = StringProperty(node, "path");
        var type = StringProperty(node, "type");
        var label = StringProperty(node, "label") ?? StringProperty(node, "partlabel");
        var protectedByLabel = IsProtectedLabel(label) || children.Any(c => c.IsProtected);
        var isSystem = !string.IsNullOrWhiteSpace(rootParent) &&
            (string.Equals(path, rootParent, StringComparison.Ordinal) || children.Any(c => c.IsSystem));
        var mountpoints = StringArrayProperty(node, "mountpoints");
        var smart = string.Equals(type, "disk", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(path)
            ? await SmartStatusAsync(path, warnings, cancellationToken)
            : null;

        var deviceWarnings = new List<string>();
        if (isSystem) deviceWarnings.Add("system-disk");
        if (protectedByLabel) deviceWarnings.Add("homeharbor-protected-label");
        if (mountpoints.Count > 0) deviceWarnings.Add("mounted");
        if (BoolProperty(node, "rm")) deviceWarnings.Add("removable");
        if (LongProperty(node, "size") < _options.MinimumInstallableBytes && string.Equals(type, "disk", StringComparison.Ordinal))
        {
            deviceWarnings.Add("small-disk");
        }
        if (smart?.Passed == false) deviceWarnings.Add("smart-failed");

        return new StorageDevice(
            Name: StringProperty(node, "name"),
            Path: path,
            SizeBytes: LongProperty(node, "size"),
            Type: type,
            Model: StringProperty(node, "model"),
            Serial: StringProperty(node, "serial"),
            Transport: StringProperty(node, "tran"),
            IsRotational: BoolProperty(node, "rota"),
            IsRemovable: BoolProperty(node, "rm"),
            Mountpoints: mountpoints,
            FileSystem: StringProperty(node, "fstype"),
            Label: label,
            Uuid: StringProperty(node, "uuid"),
            ParentKernelName: StringProperty(node, "pkname"),
            IsSystem: isSystem,
            IsProtected: protectedByLabel,
            Smart: smart,
            Warnings: deviceWarnings,
            Children: children);
    }

    private static async Task<SmartHealth?> SmartStatusAsync(string path, List<string> warnings, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var (ExitCode, Output) = await ProcessRunner.CaptureAsync(
            "smartctl",
            ["-H", "-j", path],
            TimeSpan.FromSeconds(6),
            cancellationToken);
        if (ExitCode != 0 && string.IsNullOrWhiteSpace(Output))
        {
            warnings.Add($"SMART health was unavailable for {path}.");
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(Output);
            var root = doc.RootElement;
            var passed = root.TryGetProperty("smart_status", out var smart) &&
                smart.TryGetProperty("passed", out var passedElement) &&
                passedElement.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? passedElement.GetBoolean()
                    : (bool?)null;
            var exitStatus = root.TryGetProperty("smartctl", out var smartctl) &&
                smartctl.TryGetProperty("exit_status", out var statusElement) &&
                statusElement.TryGetInt32(out var parsed)
                    ? parsed
                    : (int?)null;
            return new SmartHealth(passed, exitStatus, passed is null ? "unknown" : passed.Value ? "passed" : "failed");
        }
        catch (JsonException)
        {
            warnings.Add($"SMART health returned invalid JSON for {path}.");
            return null;
        }
    }

    private static async Task<string?> RootParentDeviceAsync(CancellationToken cancellationToken)
    {
        var (ExitCode, Output) = await ProcessRunner.CaptureAsync(
            "findmnt",
            ["-n", "-o", "SOURCE", "/"],
            TimeSpan.FromSeconds(3),
            cancellationToken);
        var source = Output.Trim();
        if (ExitCode != 0 || string.IsNullOrWhiteSpace(source) || !source.StartsWith("/dev/", StringComparison.Ordinal))
        {
            return null;
        }

        var parent = await ProcessRunner.CaptureAsync(
            "lsblk",
            ["-no", "PKNAME", source],
            TimeSpan.FromSeconds(3),
            cancellationToken);
        var parentName = parent.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
        return parent.ExitCode == 0 && !string.IsNullOrWhiteSpace(parentName)
            ? "/dev/" + parentName
            : source;
    }

    private bool IsProtectedLabel(string? label)
        => !string.IsNullOrWhiteSpace(label) &&
            _options.ProtectedPartitionLabels.Contains(label, StringComparer.Ordinal);

    private IReadOnlyList<StorageTarget> BuildTargets(IReadOnlyList<StorageDevice> devices)
    {
        var flattened = FlattenDevices(devices).ToArray();
        var targets = new List<StorageTarget>();
        foreach (var device in flattened.Where(d => string.Equals(d.Label, "data-candidate", StringComparison.Ordinal)))
        {
            var reasons = TargetEligibilityReasons(device, allowSystemPartition: true, allowProtectedCandidate: true);
            targets.Add(new StorageTarget(
                Path: device.Path ?? "",
                Kind: "main-reserved",
                SizeBytes: device.SizeBytes,
                Model: device.Model,
                Serial: device.Serial,
                Transport: device.Transport,
                Eligible: reasons.Count == 0,
                EligibilityReasons: reasons));
        }

        foreach (var disk in flattened.Where(d => d.Type == "disk"))
        {
            var reasons = TargetEligibilityReasons(disk, allowSystemPartition: false, allowProtectedCandidate: false);
            targets.Add(new StorageTarget(
                Path: disk.Path ?? "",
                Kind: "whole-disk",
                SizeBytes: disk.SizeBytes,
                Model: disk.Model,
                Serial: disk.Serial,
                Transport: disk.Transport,
                Eligible: reasons.Count == 0,
                EligibilityReasons: reasons));
        }

        return targets
            .Where(t => !string.IsNullOrWhiteSpace(t.Path))
            .GroupBy(t => t.Path + "\n" + t.Kind, StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderBy(t => t.Kind == "main-reserved" ? 0 : 1)
            .ThenByDescending(t => t.SizeBytes)
            .ToArray();
    }

    private async Task<IReadOnlyList<StorageFileSystemCapability>> DetectFileSystemsAsync(CancellationToken cancellationToken)
    {
        var btrfsAvailable = await CommandAvailableAsync("mkfs.btrfs", ["--version"], cancellationToken);
        var xfsAvailable = await CommandAvailableAsync("mkfs.xfs", ["-V"], cancellationToken);
        return
        [
            new StorageFileSystemCapability(
                FileSystem: "btrfs",
                Available: btrfsAvailable,
                UnavailableReason: btrfsAvailable ? null : "mkfs.btrfs is not available.",
                RaidModes: ["recommended", "single", "mirror", "raid10", "raid5", "raid6"],
                CanPrepareOnline: false),
            new StorageFileSystemCapability(
                FileSystem: "xfs",
                Available: xfsAvailable,
                UnavailableReason: xfsAvailable ? null : "mkfs.xfs is not available.",
                RaidModes: ["recommended", "single", "raid5", "raid6"],
                CanPrepareOnline: false),
            await DetectZfsCapabilityAsync(cancellationToken)
        ];
    }

    private async Task<StorageFileSystemCapability> DetectZfsCapabilityAsync(CancellationToken cancellationToken)
    {
        var moduleAvailable = await IsZfsModuleAvailableAsync(cancellationToken);
        var toolsAvailable = await ZfsToolsAvailableAsync(cancellationToken);
        var available = moduleAvailable && toolsAvailable;
        var reason = available
            ? null
            : !moduleAvailable
                ? "zfs kernel module is not available."
                : toolsAvailable
                    ? null
                    : "zfs-utils kernel addon is not mounted; boot the zfs kernel channel with its signed addon.";

        return new StorageFileSystemCapability(
            FileSystem: "zfs",
            Available: available,
            UnavailableReason: reason,
            RaidModes: ["recommended", "single", "mirror", "raid10", "raid5", "raid6"],
            CanPrepareOnline: false);
    }

    private Task<bool> IsZfsModuleAvailableAsync(CancellationToken cancellationToken)
        => kernelModules.IsModuleAvailableAsync("zfs", cancellationToken: cancellationToken);

    private static async Task<bool> ZfsToolsAvailableAsync(CancellationToken cancellationToken)
        => await CommandAvailableAsync("zfs", ["--version"], cancellationToken) &&
            await CommandAvailableAsync("zpool", ["version"], cancellationToken);

    private static async Task<bool> CommandAvailableAsync(
        string fileName,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var (ExitCode, _) = await ProcessRunner.CaptureAsync(fileName, args, TimeSpan.FromSeconds(5), cancellationToken);
        return ExitCode == 0;
    }

    private IReadOnlyList<string> TargetEligibilityReasons(
        StorageDevice device,
        bool allowSystemPartition,
        bool allowProtectedCandidate)
    {
        var reasons = new List<string>();
        if (string.IsNullOrWhiteSpace(device.Path)) reasons.Add("missing-path");
        if (device.IsSystem && !allowSystemPartition) reasons.Add("system-disk");
        if (device.IsProtected && !(allowProtectedCandidate && string.Equals(device.Label, "data-candidate", StringComparison.Ordinal)))
        {
            reasons.Add("homeharbor-protected-label");
        }
        if (device.Mountpoints.Count > 0 || FlattenDevices(device.Children).Any(c => c.Mountpoints.Count > 0))
        {
            reasons.Add("mounted");
        }
        if (device.IsRemovable) reasons.Add("removable");
        if (device.SizeBytes < _options.MinimumInstallableBytes) reasons.Add("small-target");
        if (device.Smart?.Passed == false) reasons.Add("smart-failed");
        return reasons;
    }

    private static string? StringProperty(JsonElement element, string name)
    {
        return !element.TryGetProperty(name, out var property) || property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
            ? null
            : property.ValueKind == JsonValueKind.String ? property.GetString() : property.ToString();
    }

    private static long LongProperty(JsonElement element, string name)
        => element.TryGetProperty(name, out var property) && property.TryGetInt64(out var value) ? value : 0;

    private static bool BoolProperty(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var property) && property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => property.TryGetInt32(out var number) && number != 0,
            JsonValueKind.String => property.GetString() is "1" or "true" or "True",
            _ => false
        };
    }

    private static IReadOnlyList<string> StringArrayProperty(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property) || property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return [];
        }

        if (property.ValueKind == JsonValueKind.Array)
        {
            return property.EnumerateArray()
                .Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : item.ToString())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!)
                .ToArray();
        }

        var single = property.ValueKind == JsonValueKind.String ? property.GetString() : property.ToString();
        return string.IsNullOrWhiteSpace(single) ? [] : [single];
    }

    private static IEnumerable<StorageDevice> FlattenDevices(IEnumerable<StorageDevice> devices)
    {
        foreach (var device in devices)
        {
            yield return device;
            foreach (var child in FlattenDevices(device.Children)) yield return child;
        }
    }

    private static class ProcessRunner
    {
        public static async Task<(int ExitCode, string Output)> CaptureAsync(
            string fileName,
            IReadOnlyList<string> args,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            try
            {
                using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutSource.CancelAfter(timeout);
                var start = new ProcessStartInfo(fileName)
                {
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    UseShellExecute = false
                };
                foreach (var arg in args) start.ArgumentList.Add(arg);

                using var process = Process.Start(start);
                if (process is null) return (127, "");
                var stdout = process.StandardOutput.ReadToEndAsync(timeoutSource.Token);
                var stderr = process.StandardError.ReadToEndAsync(timeoutSource.Token);
                await process.WaitForExitAsync(timeoutSource.Token);
                return (process.ExitCode, await stdout + await stderr);
            }
            catch (Exception ex) when (ex is Win32Exception or OperationCanceledException or IOException)
            {
                return (127, "");
            }
        }
    }
}

public static class StorageOobePlanner
{
    private const long GiB = 1024L * 1024L * 1024L;
    private const long TiB = 1024L * GiB;

    public static StorageRecommendation Recommend(StorageInventory inventory, StorageUseProfile profile)
    {
        var poolCandidates = PoolCandidateTargets(inventory).ToArray();
        var backupCandidates = inventory.Devices
            .Where(d => d.Type == "disk" && d.IsRemovable && !d.IsProtected && !d.IsSystem)
            .Select(d => d.Path)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToArray();
        var selected = poolCandidates.Select(d => d.Path).Where(p => !string.IsNullOrWhiteSpace(p)).ToArray();
        var dataProfile = selected.Length >= 2 ? "raid1" : "single";
        var metadataProfile = selected.Length >= 3 ? "raid1c3" : selected.Length == 2 ? "raid1" : "dup";
        var usableBytes = EstimateUsableBytes(poolCandidates.Select(d => d.SizeBytes).ToArray(), dataProfile);
        var oneYear = EstimateUsage(profile, years: 1);
        var threeYears = EstimateUsage(profile, years: 3);

        var warnings = new List<string>();
        if (selected.Length == 0)
        {
            warnings.Add("No unused internal data disks were found. Keep the current data root or attach disks before applying a plan.");
        }
        if (selected.Length == 1)
        {
            warnings.Add("Single-disk storage has no disk redundancy. Configure an external backup target.");
        }
        if (backupCandidates.Length > 0)
        {
            warnings.Add("External/removable disks were treated as backup targets, not primary pool members.");
        }
        if (usableBytes > 0 && threeYears > usableBytes)
        {
            warnings.Add("The three-year usage estimate exceeds the usable capacity of the recommended pool.");
        }

        var layout = selected.Length switch
        {
            0 => "current-data-root",
            1 => "single-disk-luks2-btrfs",
            2 => "two-disk-luks2-btrfs-raid1",
            _ => "multi-disk-luks2-btrfs-raid1-metadata-raid1c3"
        };
        var faultTolerance = selected.Length switch
        {
            0 => "current-system",
            1 => "none",
            2 => "one-disk",
            _ => "one-disk-with-three-copy-metadata"
        };

        return new StorageRecommendation(
            RecommendedLayout: layout,
            SelectedDevices: selected,
            BackupTargetDevices: backupCandidates,
            DataProfile: dataProfile,
            MetadataProfile: metadataProfile,
            EstimatedOneYearBytes: oneYear,
            EstimatedThreeYearBytes: threeYears,
            UsableBytes: usableBytes,
            FaultTolerance: faultTolerance,
            Warnings: warnings);
    }

    public static StoragePlan CreatePlan(StorageInventory inventory, StoragePlanRequest request)
    {
        var requestedTargets = request.Targets is { Count: > 0 }
            ? request.Targets
            : (request.SelectedDevices ?? [])
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => new StoragePlanTargetRequest(path, null))
                .ToArray();
        var uniqueTargets = requestedTargets
            .Where(target => !string.IsNullOrWhiteSpace(target.Path))
            .Select(target => target with { Path = target.Path.Trim() })
            .GroupBy(target => target.Path, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
        var selectedPaths = uniqueTargets
            .Select(target => target.Path)
            .ToArray();
        if (selectedPaths.Length == 0)
        {
            throw new InvalidOperationException("Select at least one storage target for the storage plan.");
        }

        var targets = inventory.Targets.ToDictionary(t => t.Path, StringComparer.Ordinal);
        var selected = new List<StoragePlanDevice>();
        foreach (var requested in uniqueTargets)
        {
            var path = requested.Path;
            if (!targets.TryGetValue(path, out var target))
            {
                throw new InvalidOperationException($"Selected target is not installable: {path}");
            }
            if (!string.IsNullOrWhiteSpace(requested.Kind) &&
                !string.Equals(requested.Kind, target.Kind, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Selected target kind did not match inventory: {path}");
            }
            if (!target.Eligible)
            {
                throw new InvalidOperationException($"Selected target is not eligible: {path} ({string.Join(", ", target.EligibilityReasons)})");
            }

            selected.Add(new StoragePlanDevice(
                Path: path,
                Kind: target.Kind,
                SizeBytes: target.SizeBytes,
                Model: target.Model,
                Serial: target.Serial,
                Transport: target.Transport));
        }

        var fileSystem = NormalizeFileSystem(request.FileSystem);
        var fileSystemCapability = inventory.FileSystems.FirstOrDefault(
            capability => string.Equals(capability.FileSystem, fileSystem, StringComparison.OrdinalIgnoreCase));
        if (fileSystemCapability?.Available == false)
        {
            throw new InvalidOperationException(
                $"{fileSystem} is not available for storage OOBE: {fileSystemCapability.UnavailableReason ?? "capability check failed"}");
        }

        var requestedRaidMode = NormalizeRaidMode(request.RaidMode);
        var dataProfile = "single";
        var metadataProfile = "single";
        var raidMode = "single";
        var raidBackend = "filesystem";
        var warnings = new List<string>();
        switch (fileSystem)
        {
            case "btrfs":
                raidMode = ResolveBtrfsRaidMode(selected.Count, requestedRaidMode, request.RedundancyPreference);
                if (IsMdadmRaidMode(raidMode))
                {
                    ValidateMdadmRaidMode(selected.Count, raidMode);
                    raidBackend = "mdadm";
                    dataProfile = "single";
                    metadataProfile = "dup";
                    warnings.Add(MdadmFallbackWarning(fileSystem, raidMode));
                }
                else
                {
                    dataProfile = NormalizeDataProfile(request.DataProfile) ?? BtrfsDataProfile(raidMode);
                    metadataProfile = NormalizeMetadataProfile(request.MetadataProfile) ?? BtrfsMetadataProfile(selected.Count, raidMode, request.RedundancyPreference);
                    raidMode = BtrfsRaidModeFromDataProfile(dataProfile);
                    ValidateBtrfsProfiles(selected.Count, dataProfile, metadataProfile);
                }
                break;
            case "xfs":
                if (requestedRaidMode is "raid5" or "raid6")
                {
                    raidMode = requestedRaidMode;
                    ValidateMdadmRaidMode(selected.Count, raidMode);
                    raidBackend = "mdadm";
                    warnings.Add(MdadmFallbackWarning(fileSystem, raidMode));
                }
                else
                {
                    raidMode = "single";
                    if (selected.Count != 1)
                    {
                        throw new InvalidOperationException("XFS storage plans require exactly one target unless RAID5 or RAID6 is selected.");
                    }
                    if (requestedRaidMode is not (null or "recommended" or "single"))
                    {
                        throw new InvalidOperationException("XFS storage plans support single-target mode, RAID5, or RAID6.");
                    }
                }
                if (!string.IsNullOrWhiteSpace(request.DataProfile) || !string.IsNullOrWhiteSpace(request.MetadataProfile))
                {
                    throw new InvalidOperationException("Btrfs data/metadata profiles cannot be used with XFS.");
                }
                break;
            case "zfs":
                if (!string.IsNullOrWhiteSpace(request.DataProfile) || !string.IsNullOrWhiteSpace(request.MetadataProfile))
                {
                    throw new InvalidOperationException("Btrfs data/metadata profiles cannot be used with ZFS.");
                }
                raidMode = ResolveZfsRaidMode(selected.Count, requestedRaidMode);
                ValidateZfsRaidMode(selected.Count, raidMode);
                dataProfile = raidMode;
                metadataProfile = "zfs";
                break;
            default:
                throw new InvalidOperationException("Unsupported file system: " + fileSystem);
        }

        var unlockMode = request.UnlockMode switch
        {
            null or "" => "passphrase",
            "passphrase" or "tpm2" => request.UnlockMode,
            _ => throw new InvalidOperationException("Unsupported data unlock mode: " + request.UnlockMode)
        };
        var selectedSizes = selected.Select(d => d.SizeBytes).ToArray();
        var usableBytes = EstimateUsableBytes(selectedSizes, fileSystem, dataProfile, raidMode, raidBackend);
        var planId = Guid.NewGuid().ToString("N");
        var mkfsOperation = raidBackend == "mdadm"
            ? $"mdadm --create /dev/md/homeharbor-data --level={MdadmLevel(raidMode)}"
            : fileSystem switch
            {
                "btrfs" => $"mkfs.btrfs -d {dataProfile} -m {metadataProfile}",
                "xfs" => "mkfs.xfs",
                "zfs" => "zpool create homeharbor-data " + raidMode,
                _ => throw new InvalidOperationException("Unsupported file system: " + fileSystem)
            };
        var operations = new List<string>
        {
            "validate-selected-devices",
            "luks2-format-selected-devices",
            $"configure-{unlockMode}-data-unlock"
        };
        if (raidBackend == "mdadm")
        {
            operations.Add(mkfsOperation);
            operations.Add(fileSystem switch
            {
                "btrfs" => $"mkfs.btrfs -d {dataProfile} -m {metadataProfile}",
                "xfs" => "mkfs.xfs",
                _ => throw new InvalidOperationException("Unsupported mdadm-backed file system: " + fileSystem)
            });
        }
        else
        {
            operations.Add(mkfsOperation);
        }
        operations.AddRange(
        [
            "write-homeharbor-boot-unlock-env",
            "mount-new-homeharbor-data"
        ]);

        return new StoragePlan(
            PlanId: planId,
            Layout: PlanLayout(fileSystem, raidMode, raidBackend, selected),
            Devices: selected,
            FileSystem: fileSystem,
            RaidMode: raidMode,
            RaidBackend: raidBackend,
            UnlockMode: unlockMode,
            DataProfile: dataProfile,
            MetadataProfile: metadataProfile,
            UsableBytes: usableBytes,
            Operations: operations,
            DestructiveDevices: selected.Select(d => d.Path).ToArray(),
            MountChanges:
            [
                new StorageMountChange(
                    Target: "/homeharbor-data",
                    FileSystem: fileSystem,
                    Options: MountOptions(fileSystem))
            ],
            RequiresReboot: true,
            RequiresBootloaderUnlock: unlockMode == "passphrase",
            ConfirmPhrase: "APPLY STORAGE PLAN " + planId,
            CreatedAt: DateTimeOffset.UtcNow,
            Warnings: warnings);
    }

    private static IEnumerable<StorageTarget> PoolCandidateTargets(StorageInventory inventory)
        => inventory.Targets
            .Where(t => t.Eligible && (t.Kind == "main-reserved" || !IsExternalTransport(t.Transport)))
            .OrderByDescending(d => d.SizeBytes);

    private static bool IsExternalTransport(string? transport)
        => string.Equals(transport, "usb", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(transport, "firewire", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<StorageDevice> FlattenDevices(IEnumerable<StorageDevice> devices)
    {
        foreach (var device in devices)
        {
            yield return device;
            foreach (var child in FlattenDevices(device.Children)) yield return child;
        }
    }

    private static long EstimateUsage(StorageUseProfile profile, int years)
    {
        var people = Math.Max(1, profile.FamilyMembers);
        var phones = Math.Max(0, profile.PhoneCount);
        var computers = Math.Max(0, profile.ComputerCount);
        var mediaBytes = Math.Max(0, profile.MediaLibraryTb) * TiB;
        var photoPerPhone = profile.PhotoVideoIntensity switch
        {
            "light" => 60L * GiB,
            "heavy" => 360L * GiB,
            _ => 180L * GiB
        };
        var computerBackup = 160L * GiB * computers;
        var apps = Math.Max(1, profile.Apps) * 30L * GiB;
        var familyFiles = people * 40L * GiB;
        var yearlyGrowth = phones * photoPerPhone + computerBackup + apps + familyFiles;
        return mediaBytes + yearlyGrowth * Math.Max(1, years);
    }

    private static long EstimateUsableBytes(IReadOnlyList<long> sizes, string dataProfile)
        => EstimateUsableBytes(sizes, "btrfs", dataProfile, BtrfsRaidModeFromDataProfile(dataProfile));

    private static long EstimateUsableBytes(IReadOnlyList<long> sizes, string fileSystem, string dataProfile, string raidMode, string raidBackend = "filesystem")
    {
        if (sizes.Count == 0) return 0;
        if (raidBackend == "mdadm") return MdadmUsableBytes(sizes, raidMode);
        if (fileSystem is "xfs") return sizes[0];
        if (fileSystem is "zfs")
        {
            return raidMode switch
            {
                "single" => sizes.Sum(),
                "mirror" or "raid10" => sizes.Sum() / 2,
                "raidz1" => sizes.Count < 2 ? 0 : sizes.Sum() - sizes.Min(),
                "raidz2" => sizes.Count < 3 ? 0 : sizes.OrderBy(size => size).Skip(2).Sum(),
                _ => throw new InvalidOperationException("Unsupported ZFS RAID mode: " + raidMode)
            };
        }

        return sizes.Count == 1 || dataProfile == "single" ? sizes.Sum() : sizes.Sum() / 2;
    }

    private static string NormalizeFileSystem(string? fileSystem)
        => string.IsNullOrWhiteSpace(fileSystem)
            ? "btrfs"
            : fileSystem.Trim().ToLowerInvariant() switch
            {
                "btrfs" => "btrfs",
                "xfs" => "xfs",
                "zfs" => "zfs",
                _ => throw new InvalidOperationException("Unsupported file system: " + fileSystem)
            };

    private static string? NormalizeRaidMode(string? raidMode)
        => string.IsNullOrWhiteSpace(raidMode)
            ? null
            : raidMode.Trim().ToLowerInvariant() switch
            {
                "recommended" => "recommended",
                "single" => "single",
                "mirror" => "mirror",
                "raid1" => "mirror",
                "raid5" => "raid5",
                "raid6" => "raid6",
                "raid10" => "raid10",
                "raidz1" => "raidz1",
                "raidz2" => "raidz2",
                _ => throw new InvalidOperationException("Unsupported RAID mode: " + raidMode)
            };

    private static string ResolveBtrfsRaidMode(int targetCount, string? requestedRaidMode, string? redundancyPreference)
    {
        var raidMode = requestedRaidMode;
        if (raidMode is null or "recommended")
        {
            raidMode = redundancyPreference switch
            {
                "capacity" => "single",
                "raid10" => targetCount >= 4 ? "raid10" : "mirror",
                _ => targetCount >= 2 ? "mirror" : "single"
            };
        }

        return raidMode is not ("single" or "mirror" or "raid10" or "raid5" or "raid6")
            ? throw new InvalidOperationException("Btrfs storage plans support recommended, single, mirror, RAID10, RAID5, or RAID6.")
            : raidMode;
    }

    private static string BtrfsDataProfile(string raidMode)
        => raidMode switch
        {
            "single" => "single",
            "mirror" => "raid1",
            "raid10" => "raid10",
            _ => throw new InvalidOperationException("Unsupported Btrfs RAID mode: " + raidMode)
        };

    private static string BtrfsMetadataProfile(int targetCount, string raidMode, string? redundancyPreference)
        => redundancyPreference switch
        {
            "capacity" => targetCount == 1 ? "dup" : "raid1",
            "raid10" => targetCount >= 4 ? "raid10" : targetCount == 1 ? "dup" : "raid1",
            _ => raidMode == "raid10" && targetCount >= 4
                ? "raid10"
                : targetCount >= 3 ? "raid1c3" : targetCount == 2 ? "raid1" : "dup"
        };

    private static string BtrfsRaidModeFromDataProfile(string dataProfile)
        => dataProfile switch
        {
            "single" => "single",
            "raid1" => "mirror",
            "raid10" => "raid10",
            _ => dataProfile
        };

    private static void ValidateBtrfsProfiles(int targetCount, string dataProfile, string metadataProfile)
    {
        if (dataProfile == "raid10" && targetCount < 4)
        {
            throw new InvalidOperationException("RAID10 data profile requires at least four targets.");
        }
        if ((dataProfile is "raid1" or "raid10") && targetCount < 2)
        {
            throw new InvalidOperationException(dataProfile + " data profile requires multiple targets.");
        }
        if (metadataProfile == "raid10" && targetCount < 4)
        {
            throw new InvalidOperationException("RAID10 metadata profile requires at least four targets.");
        }
        if (metadataProfile == "raid1c3" && targetCount < 3)
        {
            throw new InvalidOperationException("RAID1C3 metadata profile requires at least three targets.");
        }
        if ((metadataProfile is "raid1" or "raid10") && targetCount < 2)
        {
            throw new InvalidOperationException(metadataProfile + " metadata profile requires multiple targets.");
        }
    }

    private static string ResolveZfsRaidMode(int targetCount, string? requestedRaidMode)
    {
        if (requestedRaidMode is null or "recommended")
        {
            return targetCount switch
            {
                <= 1 => "single",
                2 => "mirror",
                3 => "raidz1",
                _ => "raidz2"
            };
        }

        return requestedRaidMode switch
        {
            "raid5" => "raidz1",
            "raid6" => "raidz2",
            _ => requestedRaidMode
        };
    }

    private static void ValidateZfsRaidMode(int targetCount, string raidMode)
    {
        switch (raidMode)
        {
            case "single":
                return;
            case "mirror":
                if (targetCount < 2) throw new InvalidOperationException("ZFS mirror requires at least two targets.");
                return;
            case "raid10":
                if (targetCount < 4 || targetCount % 2 != 0) throw new InvalidOperationException("ZFS RAID10 requires an even number of at least four targets.");
                return;
            case "raidz1":
                if (targetCount < 3) throw new InvalidOperationException("ZFS RAIDZ1 requires at least three targets.");
                return;
            case "raidz2":
                if (targetCount < 4) throw new InvalidOperationException("ZFS RAIDZ2 requires at least four targets.");
                return;
            default:
                throw new InvalidOperationException("Unsupported ZFS RAID mode: " + raidMode);
        }
    }

    private static bool IsMdadmRaidMode(string raidMode)
        => raidMode is "raid5" or "raid6";

    private static void ValidateMdadmRaidMode(int targetCount, string raidMode)
    {
        switch (raidMode)
        {
            case "raid5":
                if (targetCount < 3) throw new InvalidOperationException("RAID5 requires at least three targets.");
                return;
            case "raid6":
                if (targetCount < 4) throw new InvalidOperationException("RAID6 requires at least four targets.");
                return;
            default:
                throw new InvalidOperationException("Unsupported mdadm RAID mode: " + raidMode);
        }
    }

    private static int MdadmLevel(string raidMode)
        => raidMode switch
        {
            "raid5" => 5,
            "raid6" => 6,
            _ => throw new InvalidOperationException("Unsupported mdadm RAID mode: " + raidMode)
        };

    private static long MdadmUsableBytes(IReadOnlyList<long> sizes, string raidMode)
    {
        if (sizes.Count == 0) return 0;
        var parityDevices = raidMode switch
        {
            "raid5" => 1,
            "raid6" => 2,
            _ => throw new InvalidOperationException("Unsupported mdadm RAID mode: " + raidMode)
        };
        return sizes.Count <= parityDevices ? 0 : sizes.Min() * (sizes.Count - parityDevices);
    }

    private static string MdadmFallbackWarning(string fileSystem, string raidMode)
        => $"{fileSystem.ToUpperInvariant()} {raidMode.ToUpperInvariant()} will use mdadm underneath because the selected filesystem does not support or is not recommended for that RAID mode natively.";

    private static string PlanLayout(string fileSystem, string raidMode, string raidBackend, IReadOnlyList<StoragePlanDevice> selected)
    {
        var targetPrefix = selected.Count == 1 && selected[0].Kind == "main-reserved"
            ? "main-reserved"
            : selected.Count == 1 ? "single-disk" : selected.Count == 2 ? "two-disk" : "multi-disk";
        return raidBackend == "mdadm"
            ? $"{targetPrefix}-luks2-mdadm-{raidMode}-{fileSystem}"
            : $"{targetPrefix}-luks2-{fileSystem}-{raidMode}";
    }

    private static string MountOptions(string fileSystem)
        => fileSystem switch
        {
            "btrfs" => "noatime,compress=zstd,nofail,x-systemd.device-timeout=0",
            "xfs" => "noatime,nofail,x-systemd.device-timeout=0",
            "zfs" => "mountpoint=/homeharbor-data,atime=off,compression=zstd",
            _ => "defaults"
        };

    private static string? NormalizeDataProfile(string? profile)
        => string.IsNullOrWhiteSpace(profile)
            ? null
            : profile.Trim() switch
            {
                "single" => "single",
                "raid1" => "raid1",
                "raid10" => "raid10",
                _ => throw new InvalidOperationException("Unsupported data profile: " + profile)
            };

    private static string? NormalizeMetadataProfile(string? profile)
        => string.IsNullOrWhiteSpace(profile)
            ? null
            : profile.Trim() switch
            {
                "dup" => "dup",
                "single" => "single",
                "raid1" => "raid1",
                "raid1c3" => "raid1c3",
                "raid10" => "raid10",
                _ => throw new InvalidOperationException("Unsupported metadata profile: " + profile)
            };
}
