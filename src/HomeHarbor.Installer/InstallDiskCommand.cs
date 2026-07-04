using System.CommandLine;
using System.Diagnostics;
using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HomeHarbor.Tooling;

internal static partial class InstallDiskCommand
{
    public static Command CreateCommand()
    {
        var options = InstallDiskOptions.CreateCommandOptions();
        var command = new Command("install-disk", "Install HomeHarbor to a disk.");
        options.AddTo(command);
        command.SetAction(async (parseResult, cancellationToken) =>
            await RunAsync(InstallDiskOptions.FromParseResult(parseResult, options), cancellationToken: cancellationToken));
        return command;
    }

    public static async Task<int> RunAsync(
        string[] args,
        Action<string>? output = null,
        TextReader? input = null,
        bool? inputInteractive = null,
        CancellationToken cancellationToken = default)
        => await RunAsync(InstallDiskOptions.Parse(args), output, input, inputInteractive, cancellationToken);

    private static async Task<int> RunAsync(
        InstallDiskOptions options,
        Action<string>? output = null,
        TextReader? input = null,
        bool? inputInteractive = null,
        CancellationToken cancellationToken = default)
    {
        var io = new InstallDiskIo(
            output ?? Console.Write,
            input ?? Console.In,
            inputInteractive ?? !Console.IsInputRedirected);
        if (options.ShowHelp)
        {
            io.Write(InstallDiskOptions.Usage);
            return 0;
        }

        if (options.ListDisks)
        {
            return await InstallDiskProcess.RunStreamingAsync(
                "lsblk",
                ["-J", "-b", "-o", "NAME,PATH,SIZE,TYPE,MODEL,SERIAL,TRAN,MOUNTPOINTS"],
                io,
                cancellationToken);
        }

        var executor = new InstallDiskExecutor(options, io);
        await executor.RunAsync(cancellationToken);
        return 0;
    }
}

internal sealed record InstallDiskOptions(
    string? Target,
    string? SystemOta,
    string? SystemManifest,
    string? KernelOta,
    string? ChannelFile,
    string PublicKey,
    string? VerifyScript,
    string? Confirm,
    bool Yes,
    bool DryRun,
    bool ListDisks,
    bool ShowHelp)
{
    public const string Usage = """
        Usage:
          HomeHarbor.Installer install-disk --list-disks
          HomeHarbor.Installer install-disk --target /dev/sdX --system-ota PATH --kernel-ota PATH --public-key PATH --confirm "ERASE /dev/sdX"
            [--system-manifest PATH] [--dry-run] [--yes]

        Environment:
          HOMEHARBOR_SECURE_BOOT_MOK_ENROLL
                                          auto, force, or off; defaults to auto for secure-boot-raw-uki installs
        """;

    public static InstallDiskOptions Parse(string[] args)
    {
        var commandOptions = CreateCommandOptions();
        var command = new Command("install-disk");
        commandOptions.AddTo(command);
        var parseResult = command.Parse(args);
        return parseResult.Errors.Count > 0
            ? throw new ArgumentException(string.Join(Environment.NewLine, parseResult.Errors.Select(error => error.Message)))
            : FromParseResult(parseResult, commandOptions, args.Any(arg => arg is "-h" or "--help"));
    }

    public static CommandOptions CreateCommandOptions()
        => new(
            new Option<bool>("--list-disks") { Description = "List installable disks." },
            NullableStringOption("--target", "Target disk path."),
            NullableStringOption("--system-ota", "System OTA bundle path."),
            NullableStringOption("--system-manifest", "System OTA manifest path."),
            NullableStringOption("--kernel-ota", "Kernel OTA bundle path."),
            NullableStringOption("--channel-file", "Release channel file path."),
            StringOption("--public-key", "/etc/homeharbor/release.pub.pem", "Release public key."),
            NullableStringOption("--verify-script", "Manifest verification helper script."),
            NullableStringOption("--confirm", "Required erase confirmation phrase."),
            new Option<bool>("--yes") { Description = "Confirm non-interactively." },
            new Option<bool>("--dry-run") { Description = "Print the install plan without writing." },
            MovedOption("--data-unlock"),
            MovedOption("--data-passphrase-file"),
            MovedOption("--tpm2-pcrs"));

    public static InstallDiskOptions FromParseResult(ParseResult parseResult, CommandOptions options, bool showHelp = false)
    {
        return new InstallDiskOptions(
            parseResult.GetValue(options.Target),
            parseResult.GetValue(options.SystemOta),
            parseResult.GetValue(options.SystemManifest),
            parseResult.GetValue(options.KernelOta),
            parseResult.GetValue(options.ChannelFile),
            parseResult.GetValue(options.PublicKey)!,
            parseResult.GetValue(options.VerifyScript),
            parseResult.GetValue(options.Confirm),
            parseResult.GetValue(options.Yes),
            parseResult.GetValue(options.DryRun),
            parseResult.GetValue(options.ListDisks),
            showHelp);
    }

    private static Option<string> StringOption(string name, string defaultValue, string description)
        => new(name)
        {
            DefaultValueFactory = _ => defaultValue,
            Description = description
        };

    private static Option<string?> NullableStringOption(string name, string description)
        => new(name) { Description = description };

    private static Option<string?> MovedOption(string name)
    {
        var option = new Option<string?>(name)
        {
            Arity = ArgumentArity.ZeroOrOne,
            Description = "Moved to the Web OOBE storage setup."
        };
        option.Validators.Add(result => result.AddError(name + " has moved to the Web OOBE storage setup"));
        return option;
    }

    public sealed record CommandOptions(
        Option<bool> ListDisks,
        Option<string?> Target,
        Option<string?> SystemOta,
        Option<string?> SystemManifest,
        Option<string?> KernelOta,
        Option<string?> ChannelFile,
        Option<string> PublicKey,
        Option<string?> VerifyScript,
        Option<string?> Confirm,
        Option<bool> Yes,
        Option<bool> DryRun,
        Option<string?> DataUnlock,
        Option<string?> DataPassphraseFile,
        Option<string?> Tpm2Pcrs)
    {
        public void AddTo(Command command)
        {
            command.Options.Add(ListDisks);
            command.Options.Add(Target);
            command.Options.Add(SystemOta);
            command.Options.Add(SystemManifest);
            command.Options.Add(KernelOta);
            command.Options.Add(ChannelFile);
            command.Options.Add(PublicKey);
            command.Options.Add(VerifyScript);
            command.Options.Add(Confirm);
            command.Options.Add(Yes);
            command.Options.Add(DryRun);
            command.Options.Add(DataUnlock);
            command.Options.Add(DataPassphraseFile);
            command.Options.Add(Tpm2Pcrs);
        }
    }
}

internal sealed record AvbHashtreeDescriptor(
    string PartitionName,
    string HashAlgorithm,
    long DataBlockSize,
    long HashBlockSize,
    long DataBlocks,
    long TreeOffset,
    string Salt,
    string RootDigest)
{
    public long ImageSizeBytes => checked(DataBlockSize * DataBlocks);

    public static AvbHashtreeDescriptor Create(
        string partitionName,
        string hashAlgorithm,
        long dataBlockSize,
        long hashBlockSize,
        long dataBlocks,
        long treeOffset,
        string salt,
        string rootDigest,
        string sourceName)
    {
        if (string.IsNullOrWhiteSpace(partitionName))
        {
            throw new InvalidOperationException("AVB descriptor in " + sourceName + " is missing a partition name");
        }

        if (string.IsNullOrWhiteSpace(hashAlgorithm))
        {
            throw new InvalidOperationException("AVB descriptor in " + sourceName + " is missing a hash algorithm");
        }

        if (dataBlockSize <= 0 || hashBlockSize <= 0 || dataBlocks <= 0 || treeOffset < 0)
        {
            throw new InvalidOperationException("AVB descriptor in " + sourceName + " has invalid block geometry");
        }

        ValidateHex(salt, "salt", sourceName);
        ValidateHex(rootDigest, "root digest", sourceName);
        _ = checked(dataBlockSize * dataBlocks);
        return new AvbHashtreeDescriptor(
            partitionName,
            hashAlgorithm.Trim().ToLowerInvariant(),
            dataBlockSize,
            hashBlockSize,
            dataBlocks,
            treeOffset,
            salt.Trim().ToLowerInvariant(),
            rootDigest.Trim().ToLowerInvariant());
    }

    public static AvbHashtreeDescriptor FromInfoImageValues(IReadOnlyDictionary<string, string> values)
    {
        var partitionName = Required(values, "Partition Name", "avbtool info_image");
        var dataBlockSize = ParsePositiveLeadingLong(Required(values, "Data Block Size", "avbtool info_image"), "data block size", "avbtool info_image");
        var imageSize = ParsePositiveLeadingLong(Required(values, "Image Size", "avbtool info_image"), "image size", "avbtool info_image");
        if (imageSize % dataBlockSize != 0)
        {
            throw new InvalidOperationException("AVB descriptor in avbtool info_image has an image size that is not block aligned");
        }

        return Create(
            partitionName,
            Required(values, "Hash Algorithm", "avbtool info_image"),
            dataBlockSize,
            ParsePositiveLeadingLong(Required(values, "Hash Block Size", "avbtool info_image"), "hash block size", "avbtool info_image"),
            imageSize / dataBlockSize,
            ParseNonNegativeLeadingLong(Required(values, "Tree Offset", "avbtool info_image"), "tree offset", "avbtool info_image"),
            Required(values, "Salt", "avbtool info_image"),
            Required(values, "Root Digest", "avbtool info_image"),
            "avbtool info_image");
    }

    public bool MatchesHashtree(AvbHashtreeDescriptor other)
        => string.Equals(PartitionName, other.PartitionName, StringComparison.Ordinal) &&
           string.Equals(HashAlgorithm, other.HashAlgorithm, StringComparison.Ordinal) &&
           DataBlockSize == other.DataBlockSize &&
           HashBlockSize == other.HashBlockSize &&
           DataBlocks == other.DataBlocks &&
           TreeOffset == other.TreeOffset &&
           string.Equals(Salt, other.Salt, StringComparison.OrdinalIgnoreCase) &&
           string.Equals(RootDigest, other.RootDigest, StringComparison.OrdinalIgnoreCase);

    private static string Required(IReadOnlyDictionary<string, string> values, string key, string sourceName)
        => values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : throw new InvalidOperationException("AVB descriptor in " + sourceName + " is missing " + key);

    private static long ParsePositiveLeadingLong(string value, string name, string sourceName)
    {
        var parsed = ParseNonNegativeLeadingLong(value, name, sourceName);
        return parsed > 0
            ? parsed
            : throw new InvalidOperationException("AVB descriptor in " + sourceName + " has invalid " + name);
    }

    private static long ParseNonNegativeLeadingLong(string value, string name, string sourceName)
    {
        var token = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        return long.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed >= 0
            ? parsed
            : throw new InvalidOperationException("AVB descriptor in " + sourceName + " has invalid " + name);
    }

    private static void ValidateHex(string value, string name, string sourceName)
    {
        var hex = value.Trim();
        if (hex.Length == 0 || hex.Length % 2 != 0 || !hex.All(Uri.IsHexDigit))
        {
            throw new InvalidOperationException("AVB descriptor in " + sourceName + " has invalid " + name);
        }
    }
}

internal sealed partial class InstallDiskExecutor(InstallDiskOptions options, InstallDiskIo io)
{
    private const long MiB = 1024L * 1024L;
    private const long EspSizeMiB = 768;
    private const long BootImageBytes = 512L * MiB;
    private const long RootImageBytes = 2304L * MiB;
    private const long RecoveryImageBytes = 1648L * MiB;
    private const long ModulesImageBytes = 448L * MiB;
    private const long FirmwareImageBytes = 832L * MiB;
    private const long VbmetaImageBytes = 16L * MiB;
    private const long StateSizeMiB = 4096;
    private const long DataMinMiB = 1024;
    private const long InstallWorkMinMiB = 24576;
    private const long SuperMetadataBytes = 64L * 1024L;
    private const long SuperMetadataCopies = 2;
    private const long SuperReservedBytes = 16L * MiB;
    private const long SuperGroupBytes = 2 * RootImageBytes + 2 * ModulesImageBytes + 2 * FirmwareImageBytes;
    private const long SuperPartitionBytes = SuperGroupBytes + SuperReservedBytes;
    private const long SuperPartitionMiB = SuperPartitionBytes / MiB;
    private const string BootEnvDir = "lib/homeharbor/ota";
    private const string SecureBootPublicCertSha256 = "dd56573ce2b017f074bd2514ac9a152d0f95394a77bd6cc2c2f0f39ac538ff41";

    private readonly List<KernelAddon> _kernelAddons = [];
    private string _targetReal = string.Empty;
    private bool _targetIsBlock;
    private bool _targetIsFile;
    private string _runtimeDir = string.Empty;
    private string _work = string.Empty;
    private string _loopDevice = string.Empty;
    private bool _dataWorkMounted;
    private bool _stateMounted;
    private bool _espMounted;
    private string _systemRoot = string.Empty;
    private string _kernelRoot = string.Empty;
    private string _version = string.Empty;
    private string _channel = string.Empty;
    private string _bootMode = string.Empty;
    private string _kernelVersion = string.Empty;
    private string _kernelChannel = string.Empty;
    private string _kernelRelease = string.Empty;
    private string _kernelBootMode = string.Empty;
    private string _kernelReleaseChannel = string.Empty;
    private string _vbmetaDigestA = string.Empty;
    private string _vbmetaDigestB = string.Empty;
    private long _diskSize;
    private long _minimumBytes;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await PreflightAsync(cancellationToken);
            if (options.DryRun)
            {
                WriteDryRun();
                return;
            }

            await ConfirmTargetAsync(cancellationToken);
            await InstallAsync(cancellationToken);
        }
        finally
        {
            await CleanupAsync(cancellationToken);
        }
    }

    private async Task PreflightAsync(CancellationToken cancellationToken)
    {
        RequireInstallInputs();
        _targetReal = await ResolveTargetAsync(options.Target!, cancellationToken);
        _targetIsFile = File.Exists(_targetReal);
        _targetIsBlock = await IsBlockDeviceAsync(_targetReal, cancellationToken);
        if (!_targetIsFile && !_targetIsBlock)
        {
            throw new InvalidOperationException("target must be a block device or sparse image file: " + _targetReal);
        }

        _runtimeDir = Path.Combine(Path.GetTempPath(), "homeharbor-install-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(_runtimeDir);
        _work = Path.Combine(_runtimeDir, "preflight");
        _ = Directory.CreateDirectory(_work);

        _systemRoot = await LoadSystemManifestAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(options.KernelOta))
        {
            throw new InvalidOperationException("raw UKI installs require --kernel-ota with a generic or zfs kernel channel bundle");
        }

        _kernelRoot = await ExtractOtaManifestAsync(options.KernelOta!, cancellationToken);
        LoadPreflightManifests();
        ValidatePreflightManifestRelationship();
        await ValidateChannelMetadataAsync(cancellationToken);
        await ValidateTargetSafetyAsync(cancellationToken);
        _diskSize = await GetTargetSizeAsync(cancellationToken);
        var dataInstallMinMiB = Math.Max(DataMinMiB, InstallWorkMinMiB);
        _minimumBytes = (EspSizeMiB +
                         2 * (BootImageBytes / MiB) +
                         SuperPartitionMiB +
                         StateSizeMiB +
                         2 * (RecoveryImageBytes / MiB) +
                         2 * (VbmetaImageBytes / MiB) +
                         dataInstallMinMiB) * MiB;
        if (_diskSize < _minimumBytes)
        {
            throw new InvalidOperationException($"target is too small: {_diskSize} bytes, need at least {_minimumBytes}");
        }

        if (options.DryRun)
        {
            ValidateSystemManifestHashFields();
            ValidateKernelManifestHashFields();
        }
    }

    private void RequireInstallInputs()
    {
        if (string.IsNullOrWhiteSpace(options.Target))
        {
            throw new ArgumentException("--target is required");
        }

        if (string.IsNullOrWhiteSpace(options.SystemOta))
        {
            throw new ArgumentException("--system-ota is required");
        }

        RequireFile(options.SystemOta, "system OTA not found");
        if (!string.IsNullOrWhiteSpace(options.SystemManifest))
        {
            RequireFile(options.SystemManifest, "system manifest not found");
        }

        if (!string.IsNullOrWhiteSpace(options.KernelOta))
        {
            RequireFile(options.KernelOta, "kernel OTA not found");
        }

        RequireFile(options.PublicKey, "release public key not found");
        if (!string.IsNullOrWhiteSpace(options.VerifyScript))
        {
            RequireFile(options.VerifyScript, "verify-ota-manifest helper not found");
        }
    }

    private static void RequireFile(string path, string message)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(message + ": " + path, path);
        }
    }

    private static async Task<string> ResolveTargetAsync(string target, CancellationToken cancellationToken)
    {
        if (File.Exists(target))
        {
            return Path.GetFullPath(target);
        }

        var result = await InstallDiskProcess.CaptureAsync("readlink", ["-f", target], cancellationToken);
        return result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.Output)
            ? result.Output.Trim()
            : throw new InvalidOperationException("target does not exist: " + target);
    }

    private async Task<string> LoadSystemManifestAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(options.SystemManifest))
        {
            var manifestRoot = Path.Combine(_work, "system-manifest");
            _ = Directory.CreateDirectory(manifestRoot);
            var destination = Path.Combine(manifestRoot, "manifest.json");
            File.Copy(options.SystemManifest, destination, overwrite: true);
            await VerifyManifestAsync(destination, cancellationToken);
            return manifestRoot;
        }

        return await ExtractOtaManifestAsync(options.SystemOta!, cancellationToken);
    }

    private async Task<string> ExtractOtaManifestAsync(string bundle, CancellationToken cancellationToken)
        => await ExtractOtaAsync(bundle, manifestOnly: true, cancellationToken);

    private async Task<string> ExtractOtaAsync(string bundle, bool manifestOnly, CancellationToken cancellationToken)
    {
        var top = OtaTopDirectory(bundle);
        var root = Path.Combine(_work, top);
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }

        _ = Directory.CreateDirectory(root);
        await ExtractTarGzAsync(
            bundle,
            _work,
            manifestOnly ? top + "/manifest.json" : null,
            cancellationToken);
        var manifest = Path.Combine(root, "manifest.json");
        if (!File.Exists(manifest))
        {
            throw new InvalidOperationException("OTA bundle missing manifest.json: " + bundle);
        }

        await VerifyManifestAsync(manifest, cancellationToken);
        using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
        var packageKind = JsonString(doc.RootElement, "packageKind");
        var otaType = JsonString(doc.RootElement, "type");
        if (string.Equals(bundle, options.SystemOta, StringComparison.Ordinal))
        {
            if (packageKind != "system" && otaType != "full-system")
            {
                throw new InvalidOperationException("system OTA must be packageKind=system/type=full-system");
            }
        }

        if (!string.IsNullOrWhiteSpace(options.KernelOta) &&
            string.Equals(bundle, options.KernelOta, StringComparison.Ordinal))
        {
            if (packageKind != "kernel" && otaType != "kernel-only")
            {
                throw new InvalidOperationException("kernel OTA must be packageKind=kernel/type=kernel-only");
            }
        }

        return root;
    }

    private async Task VerifyManifestAsync(string manifest, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(options.VerifyScript))
        {
            await InstallDiskProcess.RunRequiredAsync(
                options.VerifyScript,
                [manifest, options.PublicKey],
                io,
                "manifest signature verification failed",
                stream: false,
                cancellationToken: cancellationToken);
            return;
        }

        await new OtaManifestVerifier().VerifyAsync(manifest, options.PublicKey, cancellationToken);
    }

    private static string OtaTopDirectory(string bundle)
    {
        var members = ListTarGzMembers(bundle);
        var top = TarSafety.ValidateSingleTopLevelDirectory(members, "OTA bundle");
        return top.StartsWith("homeharbor-system-ota-", StringComparison.Ordinal) ||
            top.StartsWith("homeharbor-kernel-ota-", StringComparison.Ordinal) ||
            (top.StartsWith("homeharbor-kernel-", StringComparison.Ordinal) &&
             top.Contains("-ota-", StringComparison.Ordinal))
            ? top
            : throw new InvalidOperationException("invalid OTA bundle top-level directory " + top);
    }

    private static IReadOnlyList<string> ListTarGzMembers(string bundle)
    {
        using var file = File.OpenRead(bundle);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new TarReader(gzip);
        var members = new List<string>();
        TarEntry? entry;
        while ((entry = reader.GetNextEntry()) is not null)
        {
            if (!string.IsNullOrWhiteSpace(entry.Name))
            {
                members.Add(entry.Name);
            }
        }

        return members.Count == 0 ? throw new InvalidOperationException("OTA bundle is empty: " + bundle) : (IReadOnlyList<string>)members;
    }

    private static async Task ExtractTarGzAsync(
        string bundle,
        string destination,
        string? singleMember,
        CancellationToken cancellationToken)
    {
        var destinationRoot = Path.GetFullPath(destination);
        using var file = File.OpenRead(bundle);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new TarReader(gzip);
        var found = false;
        TarEntry? entry;
        while ((entry = reader.GetNextEntry()) is not null)
        {
            if (string.IsNullOrWhiteSpace(entry.Name))
            {
                continue;
            }

            TarSafety.ValidateMemberPath(entry.Name, "OTA bundle");
            if (singleMember is not null && !string.Equals(entry.Name, singleMember, StringComparison.Ordinal))
            {
                continue;
            }

            var outputPath = Path.GetFullPath(Path.Combine(destinationRoot, entry.Name));
            if (!outputPath.StartsWith(destinationRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("unsafe OTA bundle member path: " + entry.Name);
            }

            if (entry.EntryType is TarEntryType.Directory)
            {
                _ = Directory.CreateDirectory(outputPath);
                found = true;
                continue;
            }

            if (entry.EntryType is not TarEntryType.RegularFile and not TarEntryType.V7RegularFile)
            {
                throw new InvalidOperationException("unsupported OTA bundle member type: " + entry.Name);
            }

            _ = Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            await using var output = File.Create(outputPath);
            if (entry.DataStream is not null)
            {
                await entry.DataStream.CopyToAsync(output, cancellationToken);
            }

            found = true;
        }

        if (singleMember is not null && !found)
        {
            throw new InvalidOperationException("OTA bundle missing manifest.json: " + bundle);
        }
    }

    private void LoadPreflightManifests()
    {
        using var systemDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(_systemRoot, "manifest.json")));
        using var kernelDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(_kernelRoot, "manifest.json")));
        var system = systemDoc.RootElement;
        var kernel = kernelDoc.RootElement;
        _version = JsonString(system, "version") ?? string.Empty;
        _channel = ReleaseChannel.Require(JsonString(system, "channel"), "system OTA channel");
        _bootMode = JsonString(system, "bootMode") ?? string.Empty;
        _kernelVersion = JsonString(kernel, "version") ?? string.Empty;
        _kernelChannel = KernelChannel.Require(JsonString(kernel, "kernelChannel"), "kernel OTA kernelChannel");
        _kernelRelease = JsonString(kernel, "kernelRelease") ?? string.Empty;
        _kernelBootMode = JsonString(kernel, "bootMode") ?? string.Empty;
        _kernelReleaseChannel = ReleaseChannel.Require(JsonString(kernel, "channel"), "kernel OTA channel");
        _kernelAddons.Clear();
        _kernelAddons.AddRange(ReadManifestAddons(kernel));
        if (_bootMode is "raw-uki" or "secure-boot-raw-uki")
        {
            _vbmetaDigestA = JsonString(system, "vbmetaADigest") ?? string.Empty;
            _vbmetaDigestB = JsonString(system, "vbmetaBDigest") ?? string.Empty;
        }
    }

    private void ValidatePreflightManifestRelationship()
    {
        if (!string.Equals(_kernelReleaseChannel, _channel, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"kernel OTA channel {_kernelReleaseChannel} does not match system OTA channel {_channel}");
        }

        if (!string.Equals(_kernelVersion, _version, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"kernel OTA version {_kernelVersion} does not match system OTA version {_version}");
        }

        if (_kernelChannel != "generic" && _kernelChannel != "zfs")
        {
            throw new InvalidOperationException("kernel OTA kernelChannel must be generic or zfs, got: " + (string.IsNullOrWhiteSpace(_kernelChannel) ? "missing" : _kernelChannel));
        }

        if (_kernelChannel != "zfs" && _kernelAddons.Count > 0)
        {
            throw new InvalidOperationException("kernel addons are only valid for the zfs kernel channel");
        }

        if (string.IsNullOrWhiteSpace(_kernelRelease) || !IsSafeKernelRelease(_kernelRelease))
        {
            throw new InvalidOperationException("kernel OTA kernelRelease is missing or unsafe");
        }

        switch (_bootMode)
        {
            case "raw-uki":
            case "secure-boot-raw-uki":
                break;
            case "legacy":
                throw new InvalidOperationException("legacy ESP kernel boot is not supported by the raw UKI partition layout");
            case "secure-boot-uki":
                throw new InvalidOperationException("system OTA bootMode=secure-boot-uki has been replaced by secure-boot-raw-uki");
            default:
                throw new InvalidOperationException("system OTA bootMode must be raw-uki or secure-boot-raw-uki, got: " + _bootMode);
        }

        if (!string.Equals(_kernelBootMode, _bootMode, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"kernel OTA bootMode {_kernelBootMode} does not match system OTA bootMode {_bootMode}");
        }

        if (string.IsNullOrWhiteSpace(_vbmetaDigestA) || string.IsNullOrWhiteSpace(_vbmetaDigestB))
        {
            throw new InvalidOperationException("raw UKI manifest is missing vbmeta digests");
        }
    }

    private async Task ValidateChannelMetadataAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.ChannelFile) || !File.Exists(options.ChannelFile))
        {
            return;
        }

        await using var file = File.OpenRead(options.ChannelFile);
        using var doc = await JsonDocument.ParseAsync(file, cancellationToken: cancellationToken);
        var channelName = ReleaseChannel.Require(JsonString(doc.RootElement, "channel"), "channel metadata channel");
        var channelVersion = JsonString(doc.RootElement, "currentVersion") ?? string.Empty;
        if (!string.Equals(channelName, _channel, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"channel metadata {channelName} does not match OTA channel {_channel}");
        }

        if (!string.IsNullOrWhiteSpace(channelVersion) &&
            !string.Equals(channelVersion, _version, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"channel version {channelVersion} does not match OTA version {_version}");
        }
    }

    private async Task ValidateTargetSafetyAsync(CancellationToken cancellationToken)
    {
        if (!_targetIsBlock)
        {
            return;
        }

        var rootParent = await RootParentDeviceAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(rootParent) &&
            string.Equals(_targetReal, rootParent, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("refusing to install over the currently booted system disk: " + _targetReal);
        }

        if (await TargetHasMountsAsync(_targetReal, cancellationToken))
        {
            throw new InvalidOperationException("target disk has mounted filesystems: " + _targetReal);
        }
    }

    private async Task<long> GetTargetSizeAsync(CancellationToken cancellationToken)
    {
        if (_targetIsBlock)
        {
            var result = await InstallDiskProcess.CaptureRequiredAsync("blockdev", ["--getsize64", _targetReal], cancellationToken);
            return long.Parse(result.Trim(), CultureInfo.InvariantCulture);
        }

        return new FileInfo(_targetReal).Length;
    }

    private void ValidateSystemManifestHashFields()
    {
        ValidateManifestHashField(_systemRoot, "system OTA", "rootfsHash", "rootfs.img");
        if (_bootMode is "raw-uki" or "secure-boot-raw-uki")
        {
            ValidateManifestHashField(_systemRoot, "system OTA", "vbmetaAHash", "vbmeta_a.img");
            ValidateManifestHashField(_systemRoot, "system OTA", "vbmetaBHash", "vbmeta_b.img");
        }
    }

    private void ValidateKernelManifestHashFields()
    {
        ValidateManifestHashField(_kernelRoot, "kernel OTA", "modulesHash", "modules.img");
        ValidateManifestHashField(_kernelRoot, "kernel OTA", "firmwareHash", "firmware.img");
        ValidateManifestHashField(_kernelRoot, "kernel OTA", "recoveryHash", "recovery.img");
        ValidateManifestHashField(_kernelRoot, "kernel OTA", "bootHash", "boot.efi");
    }

    private static void ValidateManifestHashField(string root, string label, string key, string file)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "manifest.json")));
        var value = JsonString(doc.RootElement, key) ?? string.Empty;
        if (!IsLowerSha256(value))
        {
            throw new InvalidOperationException($"{label} manifest is missing {key} for {file}");
        }
    }

    private void WriteDryRun()
    {
        io.WriteLine("HomeHarbor installer dry run.");
        io.WriteLine("target=" + _targetReal);
        io.WriteLine("version=" + _version);
        io.WriteLine("channel=" + _channel);
        io.WriteLine("bootMode=" + _bootMode);
        io.WriteLine("kernelChannel=" + _kernelChannel);
        io.WriteLine("kernelAddons=" + AddonList());
        io.WriteLine("systemOta=" + options.SystemOta);
        io.WriteLine("kernelOta=" + options.KernelOta);
        io.WriteLine("diskSizeBytes=" + _diskSize.ToString(CultureInfo.InvariantCulture));
        io.WriteLine("minimumBytes=" + _minimumBytes.ToString(CultureInfo.InvariantCulture));
        io.WriteLine("installWorkMinMiB=" + InstallWorkMinMiB.ToString(CultureInfo.InvariantCulture));
        io.WriteLine("dataSetup=web-oobe");
        io.WriteLine("willEnrollMok=" + (_bootMode == "secure-boot-raw-uki" && SecureBootMokEnrollMode() != "off" ? "yes" : "no"));
        io.WriteLine("willWritePartitions=esp,boot_a,boot_b,super,state,recovery_a,recovery_b,vbmeta_a,vbmeta_b,data-candidate");
    }

    private async Task ConfirmTargetAsync(CancellationToken cancellationToken)
    {
        var expected = "ERASE " + _targetReal;
        if (string.Equals(options.Confirm, expected, StringComparison.Ordinal))
        {
            return;
        }

        if (options.Yes)
        {
            throw new InvalidOperationException("--confirm must be exactly: " + expected);
        }

        if (!io.InputInteractive)
        {
            throw new InvalidOperationException("non-interactive install requires --confirm \"" + expected + "\"");
        }

        io.Write("Type \"" + expected + "\" to continue: ");
        var confirm = await io.ReadLineAsync(cancellationToken);
        if (!string.Equals(confirm, expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("confirmation did not match");
        }
    }

    private async Task InstallAsync(CancellationToken cancellationToken)
    {
        RequireTools(["avbtool", "blockdev", "dd", "lpmake", "mkfs.ext4", "mkfs.vfat", "mount", "sgdisk", "sync", "umount", "wipefs"]);
        var installTarget = _targetReal;
        if (_targetIsFile)
        {
            RequireTools(["losetup"]);
            var loop = await InstallDiskProcess.CaptureRequiredAsync(
                "losetup",
                ["--show", "--find", "--partscan", _targetReal],
                cancellationToken);
            _loopDevice = loop.Trim();
            installTarget = _loopDevice;
        }

        await CreatePartitionTableAsync(installTarget, cancellationToken);

        var espPart = PartitionPath(installTarget, 1);
        var bootAPart = PartitionPath(installTarget, 2);
        var bootBPart = PartitionPath(installTarget, 3);
        var superPart = PartitionPath(installTarget, 4);
        var statePart = PartitionPath(installTarget, 5);
        var recoveryAPart = PartitionPath(installTarget, 6);
        var recoveryBPart = PartitionPath(installTarget, 7);
        var vbmetaAPart = PartitionPath(installTarget, 8);
        var vbmetaBPart = PartitionPath(installTarget, 9);
        var dataPart = PartitionPath(installTarget, 10);

        await InstallDiskProcess.RunRequiredAsync("mkfs.ext4", ["-F", "-L", "hh-install-work", dataPart], io, cancellationToken: cancellationToken);
        _work = Path.Combine(_runtimeDir, "install-work");
        _ = Directory.CreateDirectory(_work);
        await InstallDiskProcess.RunRequiredAsync("mount", [dataPart, _work], io, cancellationToken: cancellationToken);
        _dataWorkMounted = true;

        _systemRoot = await ExtractOtaAsync(options.SystemOta!, manifestOnly: false, cancellationToken);
        _kernelRoot = await ExtractOtaAsync(options.KernelOta!, manifestOnly: false, cancellationToken);
        LoadPreflightManifests();
        await ValidateSystemPayloadHashesAsync(cancellationToken);
        await ValidateKernelPayloadHashesAsync(cancellationToken);
        await ValidateAddonPayloadHashesAsync(cancellationToken);
        await ValidateOptionalKernelBootloaderPayloadsAsync(cancellationToken);

        await InstallDiskProcess.RunRequiredAsync("mkfs.vfat", ["-F32", "-n", "esp", espPart], io, cancellationToken: cancellationToken);
        await InstallDiskProcess.RunRequiredAsync("mkfs.ext4", ["-F", "-L", "state", statePart], io, cancellationToken: cancellationToken);

        _ = Directory.CreateDirectory(Path.Combine(_work, "state"));
        _ = Directory.CreateDirectory(Path.Combine(_work, "esp"));
        await InstallDiskProcess.RunRequiredAsync("mount", [statePart, Path.Combine(_work, "state")], io, cancellationToken: cancellationToken);
        _stateMounted = true;

        var vbmetaDigestAActual = await AvbVbmetaDigestAsync(Path.Combine(_systemRoot, "vbmeta_a.img"), cancellationToken);
        var vbmetaDigestBActual = await AvbVbmetaDigestAsync(Path.Combine(_systemRoot, "vbmeta_b.img"), cancellationToken);
        if (!string.Equals(vbmetaDigestAActual, _vbmetaDigestA, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("vbmeta A digest mismatch");
        }

        if (!string.Equals(vbmetaDigestBActual, _vbmetaDigestB, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("vbmeta B digest mismatch");
        }

        var rootDescriptorA = await AvbDescriptorFromVbmetaAsync(Path.Combine(_systemRoot, "vbmeta_a.img"), "root_a", cancellationToken);
        var rootDescriptorB = await AvbDescriptorFromVbmetaAsync(Path.Combine(_systemRoot, "vbmeta_b.img"), "root_b", cancellationToken);
        var modulesDescriptorA = VerityDescriptorFromArg(Path.Combine(_kernelRoot, "modules_a.verity"), "modules_a");
        var modulesDescriptorB = VerityDescriptorFromArg(Path.Combine(_kernelRoot, "modules_b.verity"), "modules_b");
        var firmwareDescriptorA = VerityDescriptorFromArg(Path.Combine(_kernelRoot, "firmware_a.verity"), "firmware_a");
        var firmwareDescriptorB = VerityDescriptorFromArg(Path.Combine(_kernelRoot, "firmware_b.verity"), "firmware_b");
        var recoveryDescriptorA = VerityDescriptorFromArg(Path.Combine(_kernelRoot, "recovery_a.verity"), "recovery_a");
        var recoveryDescriptorB = VerityDescriptorFromArg(Path.Combine(_kernelRoot, "recovery_b.verity"), "recovery_b");

        await WriteAvbErofsLogicalPairAsync(
            Path.Combine(_systemRoot, "rootfs.img"),
            "rootfs",
            "root_a",
            "root_b",
            RootImageBytes,
            rootDescriptorA,
            rootDescriptorB,
            cancellationToken);
        await WriteAvbErofsLogicalPairAsync(
            Path.Combine(_kernelRoot, "modules.img"),
            "modules",
            "modules_a",
            "modules_b",
            ModulesImageBytes,
            modulesDescriptorA,
            modulesDescriptorB,
            cancellationToken);
        await WriteAvbErofsLogicalPairAsync(
            Path.Combine(_kernelRoot, "firmware.img"),
            "firmware",
            "firmware_a",
            "firmware_b",
            FirmwareImageBytes,
            firmwareDescriptorA,
            firmwareDescriptorB,
            cancellationToken);
        await WriteAvbErofsLogicalPairAsync(
            Path.Combine(_kernelRoot, "recovery.img"),
            "recovery",
            "recovery_a",
            "recovery_b",
            RecoveryImageBytes,
            recoveryDescriptorA,
            recoveryDescriptorB,
            cancellationToken);

        var bootEnvRoot = Path.Combine(_work, "state", BootEnvDir);
        WriteBootEnv(Path.Combine(bootEnvRoot, "boot_a.env"), "A", "A", "root_a", _kernelRelease, "modules_a", "firmware_a", "vbmeta_a", _vbmetaDigestA, rootDescriptorA.RootDigest, modulesDescriptorA.RootDigest, firmwareDescriptorA.RootDigest, _version);
        WriteBootEnv(Path.Combine(bootEnvRoot, "boot_b.env"), "B", "B", "root_b", _kernelRelease, "modules_b", "firmware_b", "vbmeta_b", _vbmetaDigestB, rootDescriptorB.RootDigest, modulesDescriptorB.RootDigest, firmwareDescriptorB.RootDigest, _version);
        WriteTextFile(Path.Combine(bootEnvRoot, "channel"), _channel + "\n", UnixFileModes.Mode640);
        WriteTextFile(Path.Combine(bootEnvRoot, "kernel-channel"), _kernelChannel + "\n", UnixFileModes.Mode640);
        await InstallKernelAddonsToStateAsync(cancellationToken);

        await BuildSuperImageAsync(cancellationToken);
        await InstallDiskProcess.RunRequiredAsync("dd", ["if=" + Path.Combine(_work, "super.img"), "of=" + superPart, "bs=4M", "conv=fsync", "status=progress"], io, cancellationToken: cancellationToken);
        await WriteRawPartitionImageAsync(Path.Combine(_kernelRoot, "boot.efi"), bootAPart, "boot A", cancellationToken);
        await WriteRawPartitionImageAsync(Path.Combine(_kernelRoot, "boot.efi"), bootBPart, "boot B", cancellationToken);
        await InstallDiskProcess.RunRequiredAsync("dd", ["if=" + Path.Combine(_work, "recovery_a.logical"), "of=" + recoveryAPart, "bs=4M", "conv=fsync", "status=progress"], io, cancellationToken: cancellationToken);
        await InstallDiskProcess.RunRequiredAsync("dd", ["if=" + Path.Combine(_work, "recovery_b.logical"), "of=" + recoveryBPart, "bs=4M", "conv=fsync", "status=progress"], io, cancellationToken: cancellationToken);
        await WriteRawPartitionImageAsync(Path.Combine(_systemRoot, "vbmeta_a.img"), vbmetaAPart, "vbmeta A", cancellationToken);
        await WriteRawPartitionImageAsync(Path.Combine(_systemRoot, "vbmeta_b.img"), vbmetaBPart, "vbmeta B", cancellationToken);

        await MountAndPopulateEspAsync(espPart, cancellationToken);

        await InstallDiskProcess.RunRequiredAsync("sync", [], io, cancellationToken: cancellationToken);
        await UnmountEspAsync(cancellationToken);
        await UnmountStateAsync(cancellationToken);
        await UnmountDataWorkAsync(cancellationToken);
        await InstallDiskProcess.RunRequiredAsync("wipefs", ["-a", dataPart], io, cancellationToken: cancellationToken);
        await InstallDiskProcess.RunRequiredAsync("sync", [], io, cancellationToken: cancellationToken);
        io.WriteLine("HomeHarbor " + _version + " installed to " + _targetReal + ".");
    }

    private async Task CreatePartitionTableAsync(string installTarget, CancellationToken cancellationToken)
    {
        await InstallDiskProcess.RunRequiredAsync("sgdisk", ["--zap-all", installTarget], io, cancellationToken: cancellationToken);
        await InstallDiskProcess.RunRequiredAsync("sgdisk", ["-n", "1:1MiB:+" + EspSizeMiB + "MiB", "-t", "1:ef00", "-c", "1:esp", installTarget], io, cancellationToken: cancellationToken);
        await InstallDiskProcess.RunRequiredAsync("sgdisk", ["-n", "2:0:+" + (BootImageBytes / MiB) + "MiB", "-t", "2:8300", "-c", "2:boot_a", installTarget], io, cancellationToken: cancellationToken);
        await InstallDiskProcess.RunRequiredAsync("sgdisk", ["-n", "3:0:+" + (BootImageBytes / MiB) + "MiB", "-t", "3:8300", "-c", "3:boot_b", installTarget], io, cancellationToken: cancellationToken);
        await InstallDiskProcess.RunRequiredAsync("sgdisk", ["-n", "4:0:+" + SuperPartitionMiB + "MiB", "-t", "4:8300", "-c", "4:super", installTarget], io, cancellationToken: cancellationToken);
        await InstallDiskProcess.RunRequiredAsync("sgdisk", ["-n", "5:0:+" + StateSizeMiB + "MiB", "-t", "5:8300", "-c", "5:state", installTarget], io, cancellationToken: cancellationToken);
        await InstallDiskProcess.RunRequiredAsync("sgdisk", ["-n", "6:0:+" + (RecoveryImageBytes / MiB) + "MiB", "-t", "6:8300", "-c", "6:recovery_a", installTarget], io, cancellationToken: cancellationToken);
        await InstallDiskProcess.RunRequiredAsync("sgdisk", ["-n", "7:0:+" + (RecoveryImageBytes / MiB) + "MiB", "-t", "7:8300", "-c", "7:recovery_b", installTarget], io, cancellationToken: cancellationToken);
        await InstallDiskProcess.RunRequiredAsync("sgdisk", ["-n", "8:0:+" + (VbmetaImageBytes / MiB) + "MiB", "-t", "8:8300", "-c", "8:vbmeta_a", installTarget], io, cancellationToken: cancellationToken);
        await InstallDiskProcess.RunRequiredAsync("sgdisk", ["-n", "9:0:+" + (VbmetaImageBytes / MiB) + "MiB", "-t", "9:8300", "-c", "9:vbmeta_b", installTarget], io, cancellationToken: cancellationToken);
        await InstallDiskProcess.RunRequiredAsync("sgdisk", ["-n", "10:0:0", "-t", "10:8309", "-c", "10:data-candidate", installTarget], io, cancellationToken: cancellationToken);
        _ = await InstallDiskProcess.RunStreamingAsync("blockdev", ["--rereadpt", installTarget], io, cancellationToken);
        await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
    }

    private async Task ValidateSystemPayloadHashesAsync(CancellationToken cancellationToken)
    {
        await ValidatePayloadHashAsync(_systemRoot, "rootfs.img", "rootfsHash", cancellationToken);
        if (_bootMode is "raw-uki" or "secure-boot-raw-uki")
        {
            await ValidatePayloadHashAsync(_systemRoot, "vbmeta_a.img", "vbmetaAHash", cancellationToken);
            await ValidatePayloadHashAsync(_systemRoot, "vbmeta_b.img", "vbmetaBHash", cancellationToken);
        }
    }

    private async Task ValidateKernelPayloadHashesAsync(CancellationToken cancellationToken)
    {
        await ValidatePayloadHashAsync(_kernelRoot, "modules.img", "modulesHash", cancellationToken);
        await ValidatePayloadHashAsync(_kernelRoot, "firmware.img", "firmwareHash", cancellationToken);
        await ValidatePayloadHashAsync(_kernelRoot, "recovery.img", "recoveryHash", cancellationToken);
        await ValidatePayloadHashAsync(_kernelRoot, "boot.efi", "bootHash", cancellationToken);
    }

    private async Task ValidateOptionalKernelBootloaderPayloadsAsync(CancellationToken cancellationToken)
    {
        if (File.Exists(Path.Combine(_kernelRoot, "HomeHarborBoot.efi")))
        {
            await ValidatePayloadHashAsync(_kernelRoot, "HomeHarborBoot.efi", "bootloaderHash", cancellationToken);
        }

        if (File.Exists(Path.Combine(_kernelRoot, "BOOTX64.EFI")))
        {
            await ValidatePayloadHashAsync(_kernelRoot, "BOOTX64.EFI", "fallbackBootHash", cancellationToken);
        }

        if (_bootMode == "secure-boot-raw-uki" && File.Exists(Path.Combine(_kernelRoot, "mmx64.efi")))
        {
            await ValidatePayloadHashAsync(_kernelRoot, "mmx64.efi", "mokManagerHash", cancellationToken);
        }
    }

    private static async Task ValidatePayloadHashAsync(string root, string file, string key, CancellationToken cancellationToken)
    {
        var payload = Path.Combine(root, file);
        var shaFile = Path.Combine(root, file + ".sha256");
        if (file == "rootfs.img" && File.Exists(Path.Combine(root, "rootfs.img.sha256")))
        {
            shaFile = Path.Combine(root, "rootfs.img.sha256");
        }

        if (!File.Exists(payload))
        {
            throw new InvalidOperationException("system OTA missing " + file);
        }

        if (!File.Exists(shaFile))
        {
            throw new InvalidOperationException("system OTA missing " + file + ".sha256");
        }

        var expected = FirstToken(await File.ReadAllTextAsync(shaFile, cancellationToken));
        var actual = await Sha256HexAsync(payload, cancellationToken);
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "manifest.json"), cancellationToken));
        var manifestValue = JsonString(doc.RootElement, key) ?? string.Empty;
        if (!string.Equals(expected, actual, StringComparison.Ordinal) ||
            !string.Equals(manifestValue, actual, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(file + " hash mismatch");
        }
    }

    private async Task ValidateAddonPayloadHashesAsync(CancellationToken cancellationToken)
    {
        foreach (var addon in _kernelAddons)
        {
            var path = Path.Combine(_kernelRoot, addon.File);
            if (!File.Exists(path))
            {
                throw new InvalidOperationException("kernel OTA missing addon image: " + addon.File);
            }

            var actual = await Sha256HexAsync(path, cancellationToken);
            if (!string.Equals(actual, addon.Sha256, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("kernel addon " + addon.Key + " hash mismatch");
            }
        }
    }

    private static async Task WriteErofsLogicalAsync(string erofsImage, string logicalImage, long logicalBytes, string label, CancellationToken cancellationToken)
    {
        var erofsSize = new FileInfo(erofsImage).Length;
        if (erofsSize > logicalBytes)
        {
            throw new InvalidOperationException($"{label} EROFS is larger than logical area: {erofsSize} > {logicalBytes}");
        }

        await using var input = File.OpenRead(erofsImage);
        await using var output = new FileStream(logicalImage, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        output.SetLength(logicalBytes);
        await input.CopyToAsync(output, cancellationToken);
    }

    private async Task WriteAvbErofsLogicalPairAsync(
        string erofsImage,
        string label,
        string partitionA,
        string partitionB,
        long partitionBytes,
        AvbHashtreeDescriptor descriptorA,
        AvbHashtreeDescriptor descriptorB,
        CancellationToken cancellationToken)
    {
        if (descriptorA.ImageSizeBytes != descriptorB.ImageSizeBytes)
        {
            throw new InvalidOperationException($"{label} AVB slots disagree on EROFS data size");
        }

        var logicalA = Path.Combine(_work, partitionA + ".logical");
        var logicalB = Path.Combine(_work, partitionB + ".logical");
        await WriteErofsLogicalAsync(erofsImage, logicalA, descriptorA.ImageSizeBytes, label, cancellationToken);
        File.Copy(logicalA, logicalB, overwrite: true);
        await AddHashtreeFooterAsync(logicalA, partitionBytes, partitionA, descriptorA, cancellationToken);
        await AddHashtreeFooterAsync(logicalB, partitionBytes, partitionB, descriptorB, cancellationToken);
    }

    private async Task AddHashtreeFooterAsync(
        string image,
        long partitionBytes,
        string partitionName,
        AvbHashtreeDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        ValidateDescriptorForHashtreeFooter(image, partitionBytes, partitionName, descriptor);
        var generatedVbmeta = Path.Combine(_work, partitionName + ".install.vbmeta");
        await InstallDiskProcess.RunRequiredAsync(
            "avbtool",
            [
                "add_hashtree_footer",
                "--image", image,
                "--partition_size", partitionBytes.ToString(CultureInfo.InvariantCulture),
                "--partition_name", partitionName,
                "--hash_algorithm", descriptor.HashAlgorithm,
                "--salt", descriptor.Salt,
                "--block_size", descriptor.DataBlockSize.ToString(CultureInfo.InvariantCulture),
                "--do_not_generate_fec",
                "--do_not_use_ab",
                "--output_vbmeta_image", generatedVbmeta,
                "--do_not_append_vbmeta_image",
                "--algorithm", "NONE"
            ],
            io,
            stream: false,
            cancellationToken: cancellationToken);

        var actual = await AvbDescriptorFromVbmetaAsync(generatedVbmeta, partitionName, cancellationToken);
        if (!descriptor.MatchesHashtree(actual))
        {
            throw new InvalidOperationException(partitionName + " generated AVB hashtree descriptor mismatch");
        }

        await using var stream = new FileStream(image, FileMode.Open, FileAccess.Write, FileShare.None);
        stream.SetLength(partitionBytes);
    }

    private static void ValidateDescriptorForHashtreeFooter(
        string image,
        long partitionBytes,
        string partitionName,
        AvbHashtreeDescriptor descriptor)
    {
        if (!string.Equals(descriptor.PartitionName, partitionName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"AVB descriptor partition mismatch: expected {partitionName}, got {descriptor.PartitionName}");
        }

        if (descriptor.DataBlockSize != descriptor.HashBlockSize)
        {
            throw new InvalidOperationException(partitionName + " AVB descriptor uses different data and hash block sizes");
        }

        if (descriptor.TreeOffset != descriptor.ImageSizeBytes)
        {
            throw new InvalidOperationException(partitionName + " AVB tree offset does not follow the padded EROFS image");
        }

        if (descriptor.ImageSizeBytes >= partitionBytes)
        {
            throw new InvalidOperationException(partitionName + " AVB data image does not leave room for a hashtree");
        }

        var imageBytes = new FileInfo(image).Length;
        if (imageBytes != descriptor.ImageSizeBytes)
        {
            throw new InvalidOperationException($"{partitionName} logical data size mismatch: {imageBytes} != {descriptor.ImageSizeBytes}");
        }
    }

    private static async Task<string> AvbVbmetaDigestAsync(string image, CancellationToken cancellationToken)
    {
        var output = await InstallDiskProcess.CaptureRequiredAsync("avbtool", ["calculate_vbmeta_digest", "--image", image], cancellationToken);
        return FirstToken(output);
    }

    private static async Task<AvbHashtreeDescriptor> AvbDescriptorFromVbmetaAsync(string image, string partitionName, CancellationToken cancellationToken)
    {
        var output = await InstallDiskProcess.CaptureRequiredAsync("avbtool", ["info_image", "--image", image], cancellationToken);
        return ParseAvbInfoImageDescriptor(output, partitionName);
    }

    internal static AvbHashtreeDescriptor ParseAvbInfoImageDescriptor(string output, string partitionName)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        AvbHashtreeDescriptor? found = null;

        void Emit()
        {
            if (values.TryGetValue("Partition Name", out var part) &&
                string.Equals(part, partitionName, StringComparison.Ordinal))
            {
                found = AvbHashtreeDescriptor.FromInfoImageValues(values);
            }
        }

        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("Hashtree descriptor:", StringComparison.Ordinal))
            {
                Emit();
                values.Clear();
                continue;
            }

            var separator = line.IndexOf(':');
            if (separator > 0)
            {
                values[line[..separator].Trim()] = line[(separator + 1)..].Trim();
            }
        }

        Emit();
        return found ?? throw new InvalidOperationException("vbmeta image is missing descriptor for " + partitionName);
    }

    internal static AvbHashtreeDescriptor VerityDescriptorFromArg(string file, string partitionName)
    {
        if (!File.Exists(file))
        {
            throw new InvalidOperationException("kernel OTA missing verity sidecar: " + Path.GetFileName(file));
        }

        var arg = File.ReadLines(file).FirstOrDefault(line => !string.IsNullOrWhiteSpace(line))?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        return ParseVerityDescriptorArg(arg, partitionName, Path.GetFileName(file));
    }

    internal static AvbHashtreeDescriptor ParseVerityDescriptorArg(string arg, string partitionName, string sourceName)
    {
        var parts = arg.Split(':');
        if (parts.Length != 7)
        {
            throw new InvalidOperationException("invalid verity descriptor in " + sourceName);
        }

        return AvbHashtreeDescriptor.Create(
            partitionName,
            parts[0],
            ParsePositiveLong(parts[1], "data block size", sourceName),
            ParsePositiveLong(parts[2], "hash block size", sourceName),
            ParsePositiveLong(parts[3], "data blocks", sourceName),
            ParseNonNegativeLong(parts[4], "tree offset", sourceName),
            parts[5],
            parts[6],
            sourceName);
    }

    private static long ParsePositiveLong(string value, string name, string sourceName)
    {
        var parsed = ParseNonNegativeLong(value, name, sourceName);
        return parsed > 0
            ? parsed
            : throw new InvalidOperationException("invalid " + name + " in " + sourceName);
    }

    private static long ParseNonNegativeLong(string value, string name, string sourceName)
        => long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed >= 0
            ? parsed
            : throw new InvalidOperationException("invalid " + name + " in " + sourceName);

    private void WriteBootEnv(
        string path,
        string bootSlot,
        string rootSlot,
        string rootLogical,
        string kernelRelease,
        string modulesLogical,
        string firmwareLogical,
        string vbmetaPartition,
        string vbmetaDigest,
        string rootDescriptorDigest,
        string modulesDescriptorDigest,
        string firmwareDescriptorDigest,
        string version)
    {
        var builder = new StringBuilder();
        _ = builder.AppendLine("HOMEHARBOR_BOOT_SLOT=" + bootSlot);
        _ = builder.AppendLine("HOMEHARBOR_SLOT=" + rootSlot);
        _ = builder.AppendLine("HOMEHARBOR_ROOT_LOGICAL=" + rootLogical);
        _ = builder.AppendLine("HOMEHARBOR_KERNEL_RELEASE=" + kernelRelease);
        _ = builder.AppendLine("HOMEHARBOR_MODULES_LOGICAL=" + modulesLogical);
        _ = builder.AppendLine("HOMEHARBOR_FIRMWARE_LOGICAL=" + firmwareLogical);
        _ = builder.AppendLine("HOMEHARBOR_VBMETA_PARTITION=" + vbmetaPartition);
        _ = builder.AppendLine("HOMEHARBOR_VBMETA_DIGEST=" + vbmetaDigest);
        _ = builder.AppendLine("HOMEHARBOR_ROOT_DESCRIPTOR_DIGEST=" + rootDescriptorDigest);
        _ = builder.AppendLine("HOMEHARBOR_MODULES_DESCRIPTOR_DIGEST=" + modulesDescriptorDigest);
        _ = builder.AppendLine("HOMEHARBOR_FIRMWARE_DESCRIPTOR_DIGEST=" + firmwareDescriptorDigest);
        _ = builder.AppendLine("HOMEHARBOR_VERSION=" + version);
        var addonList = AddonList();
        if (!string.IsNullOrWhiteSpace(addonList))
        {
            _ = builder.AppendLine("HOMEHARBOR_ADDONS=" + addonList);
            foreach (var addon in _kernelAddons)
            {
                _ = builder.AppendLine("HOMEHARBOR_ADDON_" + AddonEnvSuffix(addon.Key) + "_SHA256=" + addon.Sha256);
            }
        }

        WriteTextFile(path, builder.ToString(), UnixFileModes.Mode644);
    }

    private async Task InstallKernelAddonsToStateAsync(CancellationToken cancellationToken)
    {
        if (_kernelAddons.Count == 0)
        {
            return;
        }

        var store = Path.Combine(_work, "state", BootEnvDir, "addons", "store");
        _ = Directory.CreateDirectory(store);
        SetMode(store, UnixFileModes.Mode750);
        foreach (var addon in _kernelAddons)
        {
            var source = Path.Combine(_kernelRoot, addon.File);
            var destination = Path.Combine(store, addon.Sha256 + ".erofs");
            var temp = destination + ".tmp." + Environment.ProcessId;
            File.Copy(source, temp, overwrite: true);
            SetMode(temp, UnixFileModes.Mode640);
            var actual = await Sha256HexAsync(temp, cancellationToken);
            if (!string.Equals(actual, addon.Sha256, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("copied kernel addon " + addon.Key + " hash mismatch");
            }

            File.Move(temp, destination, overwrite: true);
        }
    }

    private async Task BuildSuperImageAsync(CancellationToken cancellationToken)
    {
        await InstallDiskProcess.RunRequiredAsync(
            "lpmake",
            [
                "--metadata-size", SuperMetadataBytes.ToString(CultureInfo.InvariantCulture),
                "--metadata-slots", SuperMetadataCopies.ToString(CultureInfo.InvariantCulture),
                "--device-size", SuperPartitionBytes.ToString(CultureInfo.InvariantCulture),
                "--super-name", "super",
                "--group", "homeharbor:" + SuperGroupBytes,
                "--partition", "root_a:readonly:" + RootImageBytes + ":homeharbor",
                "--partition", "root_b:readonly:" + RootImageBytes + ":homeharbor",
                "--partition", "modules_a:readonly:" + ModulesImageBytes + ":homeharbor",
                "--partition", "modules_b:readonly:" + ModulesImageBytes + ":homeharbor",
                "--partition", "firmware_a:readonly:" + FirmwareImageBytes + ":homeharbor",
                "--partition", "firmware_b:readonly:" + FirmwareImageBytes + ":homeharbor",
                "--image", "root_a=" + Path.Combine(_work, "root_a.logical"),
                "--image", "root_b=" + Path.Combine(_work, "root_b.logical"),
                "--image", "modules_a=" + Path.Combine(_work, "modules_a.logical"),
                "--image", "modules_b=" + Path.Combine(_work, "modules_b.logical"),
                "--image", "firmware_a=" + Path.Combine(_work, "firmware_a.logical"),
                "--image", "firmware_b=" + Path.Combine(_work, "firmware_b.logical"),
                "--output", Path.Combine(_work, "super.img"),
                "--force-full-image"
            ],
            io,
            cancellationToken: cancellationToken);
    }

    private async Task WriteRawPartitionImageAsync(string image, string device, string label, CancellationToken cancellationToken)
    {
        if (!File.Exists(image))
        {
            throw new InvalidOperationException(label + " image is missing: " + image);
        }

        if (!await IsBlockDeviceAsync(device, cancellationToken))
        {
            throw new InvalidOperationException(label + " block device is missing: " + device);
        }

        var imageSize = new FileInfo(image).Length;
        var deviceSizeText = await InstallDiskProcess.CaptureRequiredAsync("blockdev", ["--getsize64", device], cancellationToken);
        var deviceSize = long.Parse(deviceSizeText.Trim(), CultureInfo.InvariantCulture);
        if (imageSize > deviceSize)
        {
            throw new InvalidOperationException($"{label} image is larger than its raw partition: {imageSize} > {deviceSize}");
        }

        var zeroBlockSize = 4L * MiB;
        var zeroBlocks = deviceSize / zeroBlockSize;
        var zeroRemainder = deviceSize % zeroBlockSize;
        var zeroOffset = zeroBlocks * zeroBlockSize;
        if (zeroBlocks > 0)
        {
            await InstallDiskProcess.RunRequiredAsync(
                "dd",
                ["if=/dev/zero", "of=" + device, "bs=" + zeroBlockSize, "count=" + zeroBlocks, "conv=fsync", "status=none"],
                io,
                cancellationToken: cancellationToken);
        }

        if (zeroRemainder > 0)
        {
            await InstallDiskProcess.RunRequiredAsync(
                "dd",
                ["if=/dev/zero", "of=" + device, "bs=1", "count=" + zeroRemainder, "seek=" + zeroOffset, "conv=fsync,notrunc", "status=none"],
                io,
                cancellationToken: cancellationToken);
        }

        await InstallDiskProcess.RunRequiredAsync(
            "dd",
            ["if=" + image, "of=" + device, "bs=4M", "conv=fsync,notrunc", "status=progress"],
            io,
            cancellationToken: cancellationToken);
    }

    private async Task MountAndPopulateEspAsync(string espPart, CancellationToken cancellationToken)
    {
        var esp = Path.Combine(_work, "esp");
        await InstallDiskProcess.RunRequiredAsync("mount", [espPart, esp], io, cancellationToken: cancellationToken);
        _espMounted = true;
        var selector = Path.Combine(_work, "HomeHarborBoot.efi");
        var kernelSelector = Path.Combine(_kernelRoot, "HomeHarborBoot.efi");
        if (File.Exists(kernelSelector))
        {
            File.Copy(kernelSelector, selector, overwrite: true);
            SetMode(selector, UnixFileModes.Mode644);
        }
        else
        {
            var root = FindHomeHarborRoot();
            var builder = Path.Combine(root, "src", "HomeHarbor.ImageBuilder", "HomeHarbor.ImageBuilder.csproj");
            if (!File.Exists(builder))
            {
                throw new InvalidOperationException("kernel OTA does not contain HomeHarborBoot.efi and local EFI loader builder is unavailable");
            }

            await InstallDiskProcess.RunRequiredAsync(
                "dotnet",
                ["run", "--project", builder, "--", "build-efi-loader", selector, root],
                io,
                cancellationToken: cancellationToken);
            await SignEfiFileAsync(selector, cancellationToken);
        }

        await InstallBootSelectorAsync(esp, selector, cancellationToken);
        var fallbackBoot = Path.Combine(_kernelRoot, "BOOTX64.EFI");
        if (File.Exists(fallbackBoot))
        {
            InstallFile(fallbackBoot, Path.Combine(esp, "EFI", "BOOT", "BOOTX64.EFI"), UnixFileModes.Mode644);
        }
        else
        {
            InstallFile(selector, Path.Combine(esp, "EFI", "BOOT", "BOOTX64.EFI"), UnixFileModes.Mode644);
        }

        if (_bootMode == "secure-boot-raw-uki")
        {
            InstallFile(selector, Path.Combine(esp, "EFI", "BOOT", "grubx64.efi"), UnixFileModes.Mode644);
            InstallFile(Path.Combine(_kernelRoot, "mmx64.efi"), Path.Combine(esp, "EFI", "BOOT", "mmx64.efi"), UnixFileModes.Mode644);
        }

        BootState.Initialize(esp, "A", "A", "A");
        if (SecureBootEnabled())
        {
            ValidateSecureBootConfig();
            await InstallSecureBootEnrollmentAsync(esp, cancellationToken);
        }

        if (_bootMode == "secure-boot-raw-uki")
        {
            await InstallSecureBootMokAsync(cancellationToken);
        }
    }

    private async Task InstallBootSelectorAsync(string esp, string selector, CancellationToken cancellationToken)
    {
        var homeHarborSelector = Path.Combine(esp, "EFI", "HomeHarbor", "HomeHarborBoot.efi");
        var fallbackSelector = Path.Combine(esp, "EFI", "BOOT", "BOOTX64.EFI");
        InstallFile(selector, homeHarborSelector, UnixFileModes.Mode644);
        InstallFile(selector, fallbackSelector, UnixFileModes.Mode644);
        await SignEfiFileAsync(homeHarborSelector, cancellationToken);
        await SignEfiFileAsync(fallbackSelector, cancellationToken);
    }

    private async Task SignEfiFileAsync(string path, CancellationToken cancellationToken)
    {
        if (!SecureBootEnabled() || !File.Exists(path))
        {
            return;
        }

        var temp = path + ".signed." + Environment.ProcessId;
        try
        {
            await InstallDiskProcess.RunRequiredAsync(
                "sbsign",
                ["--key", RequiredEnvironment("HOMEHARBOR_SECURE_BOOT_KEY"), "--cert", RequiredEnvironment("HOMEHARBOR_SECURE_BOOT_CERT"), "--output", temp, path],
                io,
                cancellationToken: cancellationToken);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            TryDeleteFile(temp);
        }
    }

    private async Task InstallSecureBootEnrollmentAsync(string esp, CancellationToken cancellationToken)
    {
        if (!SecureBootEnabled())
        {
            return;
        }

        var enrollMode = SecureBootEnrollMode();
        if (enrollMode == "off")
        {
            return;
        }

        var uuid = (await InstallDiskProcess.CaptureRequiredAsync("systemd-id128", ["new", "--uuid"], cancellationToken)).Trim();
        var work = Path.Combine(_runtimeDir, "secure-boot-enroll-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(work);
        try
        {
            var der = Path.Combine(work, "secure-boot.der");
            await InstallDiskProcess.RunRequiredAsync(
                "openssl",
                ["x509", "-outform", "DER", "-in", RequiredEnvironment("HOMEHARBOR_SECURE_BOOT_CERT"), "-out", der],
                io,
                cancellationToken: cancellationToken);
            foreach (var key in new[] { "PK", "KEK", "db" })
            {
                await InstallDiskProcess.RunRequiredAsync(
                    "sbsiglist",
                    ["--owner", uuid, "--type", "x509", "--output", Path.Combine(work, key + ".esl"), der],
                    io,
                    cancellationToken: cancellationToken);
            }

            var keysDir = Path.Combine(esp, "loader", "keys", "auto");
            _ = Directory.CreateDirectory(keysDir);
            SetMode(keysDir, UnixFileModes.Mode755);
            const string attr = "NON_VOLATILE,RUNTIME_ACCESS,BOOTSERVICE_ACCESS,TIME_BASED_AUTHENTICATED_WRITE_ACCESS";
            foreach (var key in new[] { "PK", "KEK", "db" })
            {
                await InstallDiskProcess.RunRequiredAsync(
                    "sbvarsign",
                    [
                        "--attr", attr,
                        "--key", RequiredEnvironment("HOMEHARBOR_SECURE_BOOT_KEY"),
                        "--cert", RequiredEnvironment("HOMEHARBOR_SECURE_BOOT_CERT"),
                        "--output", Path.Combine(keysDir, key + ".auth"),
                        key,
                        Path.Combine(work, key + ".esl")
                    ],
                    io,
                    cancellationToken: cancellationToken);
            }

            var loaderConf = Path.Combine(esp, "loader", "loader.conf");
            _ = Directory.CreateDirectory(Path.GetDirectoryName(loaderConf)!);
            var existing = File.Exists(loaderConf)
                ? File.ReadAllLines(loaderConf).Where(line =>
                    !line.StartsWith("secure-boot-enroll", StringComparison.Ordinal) &&
                    !line.StartsWith("secure-boot-enroll-action", StringComparison.Ordinal) &&
                    !line.StartsWith("secure-boot-enroll-timeout-sec", StringComparison.Ordinal)).ToList()
                : [];
            existing.Add("secure-boot-enroll " + enrollMode);
            existing.Add("secure-boot-enroll-action reboot");
            File.WriteAllLines(loaderConf, existing);
            SetMode(loaderConf, UnixFileModes.Mode644);
        }
        finally
        {
            TryDeleteDirectory(work);
        }
    }

    private async Task InstallSecureBootMokAsync(CancellationToken cancellationToken)
    {
        var enrollMode = SecureBootMokEnrollMode();
        if (enrollMode is not ("auto" or "force" or "off"))
        {
            throw new InvalidOperationException("HOMEHARBOR_SECURE_BOOT_MOK_ENROLL must be auto, force, or off; got: " + enrollMode);
        }

        if (enrollMode == "off")
        {
            return;
        }

        if (!OperatingSystem.IsLinux() || Environment.UserName != "root")
        {
            throw new InvalidOperationException("MOK enrollment must run as root");
        }

        if (Environment.GetEnvironmentVariable("HOMEHARBOR_TEST_EFI_AVAILABLE") != "1" &&
            !Directory.Exists("/sys/firmware/efi/efivars"))
        {
            throw new InvalidOperationException("MOK enrollment requires a UEFI boot with efivarfs available");
        }

        RequireTools(["mokutil", "openssl"]);
        var cert = SecureBootPublicCert();
        var work = Path.Combine(_runtimeDir, "mok-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(work);
        try
        {
            var der = Path.Combine(work, "homeharbor-secure-boot.der");
            await ConvertSecureBootCertToDerAsync(cert, der, cancellationToken);
            var test = await InstallDiskProcess.CaptureAsync("mokutil", ["--test-key", der], cancellationToken);
            if (test.ExitCode == 0)
            {
                io.Write(test.Output);
                io.WriteLine("HomeHarbor Secure Boot certificate is already enrolled in MOK.");
                return;
            }

            if (test.Output.Contains("not enrolled", StringComparison.Ordinal) ||
                test.Output.Contains("is not enrolled", StringComparison.Ordinal))
            {
                io.Write(test.Output);
            }
            else
            {
                io.WriteLine("warning: could not determine whether the HomeHarbor MOK is already enrolled: " + test.Output.Trim());
            }

            if (enrollMode == "auto" && !io.InputInteractive)
            {
                throw new InvalidOperationException("MOK enrollment needs an interactive terminal to set the one-time password; rerun interactively or set HOMEHARBOR_SECURE_BOOT_MOK_ENROLL=off");
            }

            io.WriteLine("");
            io.WriteLine("Queuing HomeHarbor Secure Boot certificate for MOK enrollment.");
            io.WriteLine("mokutil will ask for a one-time password. After reboot, choose Enroll MOK in MOK Manager and enter that password.");
            await InstallDiskProcess.RunRequiredAsync("mokutil", ["--import", der], io, cancellationToken: cancellationToken);
            io.WriteLine("HomeHarbor MOK enrollment request queued. Reboot before enabling Secure Boot.");
        }
        finally
        {
            TryDeleteDirectory(work);
        }
    }

    private async Task ConvertSecureBootCertToDerAsync(string cert, string der, CancellationToken cancellationToken)
    {
        var result = await InstallDiskProcess.RunStreamingAsync("openssl", ["x509", "-in", cert, "-outform", "DER", "-out", der], io, cancellationToken);
        if (result != 0)
        {
            await InstallDiskProcess.RunRequiredAsync("openssl", ["x509", "-inform", "DER", "-in", cert, "-outform", "DER", "-out", der], io, "failed to parse HomeHarbor Secure Boot certificate: " + cert, cancellationToken: cancellationToken);
        }

        var actualHash = await Sha256HexAsync(der, cancellationToken);
        if (!string.Equals(actualHash, SecureBootPublicCertSha256, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"HomeHarbor Secure Boot certificate SHA256 mismatch: expected {SecureBootPublicCertSha256}, got {actualHash}");
        }
    }

    private static string SecureBootPublicCert()
    {
        var candidates = new[]
        {
            Environment.GetEnvironmentVariable("HOMEHARBOR_SECURE_BOOT_MOK_CERT"),
            Environment.GetEnvironmentVariable("HOMEHARBOR_SECURE_BOOT_PUBLIC_CERT"),
            Environment.GetEnvironmentVariable("HOMEHARBOR_SECURE_BOOT_CERT"),
            Path.Combine(FindHomeHarborRoot(), "certs", "homeharbor-secure-boot.crt"),
            "/usr/share/homeharbor/secure-boot/homeharbor-secure-boot.crt",
            "/etc/homeharbor/homeharbor-secure-boot.crt"
        };
        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("HomeHarbor Secure Boot public certificate was not found");
    }

    private static void ValidateSecureBootConfig()
    {
        var key = Environment.GetEnvironmentVariable("HOMEHARBOR_SECURE_BOOT_KEY");
        if (string.IsNullOrWhiteSpace(key) || !File.Exists(key))
        {
            throw new InvalidOperationException("HOMEHARBOR_SECURE_BOOT_KEY must point to the Secure Boot signing key when HOMEHARBOR_SECURE_BOOT=1");
        }

        var cert = Environment.GetEnvironmentVariable("HOMEHARBOR_SECURE_BOOT_CERT");
        if (string.IsNullOrWhiteSpace(cert) || !File.Exists(cert))
        {
            throw new InvalidOperationException("HOMEHARBOR_SECURE_BOOT_CERT must point to the Secure Boot signing certificate when HOMEHARBOR_SECURE_BOOT=1");
        }

        var enrollMode = SecureBootEnrollMode();
        if (enrollMode is not ("manual" or "force" or "off"))
        {
            throw new InvalidOperationException("HOMEHARBOR_SECURE_BOOT_ENROLL must be manual, force, or off; got: " + enrollMode);
        }

        if (enrollMode != "off")
        {
            RequireTools(["openssl", "sbsiglist", "sbvarsign", "systemd-id128"]);
        }
    }

    private static bool SecureBootEnabled()
        => Environment.GetEnvironmentVariable("HOMEHARBOR_SECURE_BOOT") == "1";

    private static string SecureBootEnrollMode()
        => Environment.GetEnvironmentVariable("HOMEHARBOR_SECURE_BOOT_ENROLL") ?? "manual";

    private static string SecureBootMokEnrollMode()
        => Environment.GetEnvironmentVariable("HOMEHARBOR_SECURE_BOOT_MOK_ENROLL") ?? "auto";

    private static string RequiredEnvironment(string name)
        => Environment.GetEnvironmentVariable(name)
           ?? throw new InvalidOperationException(name + " is required");

    private async Task UnmountEspAsync(CancellationToken cancellationToken)
    {
        if (_espMounted)
        {
            await InstallDiskProcess.RunRequiredAsync("umount", [Path.Combine(_work, "esp")], io, cancellationToken: cancellationToken);
            _espMounted = false;
        }
    }

    private async Task UnmountStateAsync(CancellationToken cancellationToken)
    {
        if (_stateMounted)
        {
            await InstallDiskProcess.RunRequiredAsync("umount", [Path.Combine(_work, "state")], io, cancellationToken: cancellationToken);
            _stateMounted = false;
        }
    }

    private async Task UnmountDataWorkAsync(CancellationToken cancellationToken)
    {
        if (_dataWorkMounted)
        {
            await InstallDiskProcess.RunRequiredAsync("umount", [_work], io, cancellationToken: cancellationToken);
            _dataWorkMounted = false;
        }
    }

    private async Task CleanupAsync(CancellationToken cancellationToken)
    {
        await TryRunCleanupAsync(_espMounted, "umount", [Path.Combine(_work, "esp")], cancellationToken);
        _espMounted = false;
        await TryRunCleanupAsync(_stateMounted, "umount", [Path.Combine(_work, "state")], cancellationToken);
        _stateMounted = false;
        await TryRunCleanupAsync(_dataWorkMounted, "umount", [_work], cancellationToken);
        _dataWorkMounted = false;
        await TryRunCleanupAsync(!string.IsNullOrWhiteSpace(_loopDevice), "losetup", ["-d", _loopDevice], cancellationToken);
        _loopDevice = string.Empty;
        TryDeleteDirectory(_runtimeDir);
    }

    private async Task TryRunCleanupAsync(bool condition, string fileName, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        if (!condition)
        {
            return;
        }

        try
        {
            _ = await InstallDiskProcess.RunStreamingAsync(fileName, args, io, cancellationToken);
        }
        catch (Exception)
        {
        }
    }

    private static async Task<bool> IsBlockDeviceAsync(string path, CancellationToken cancellationToken)
    {
        var result = await InstallDiskProcess.CaptureAsync("test", ["-b", path], cancellationToken);
        return result.ExitCode == 0;
    }

    private static async Task<bool> TargetHasMountsAsync(string device, CancellationToken cancellationToken)
    {
        var result = await InstallDiskProcess.CaptureAsync("lsblk", ["-nrpo", "MOUNTPOINTS", device], cancellationToken);
        return result.ExitCode == 0 && result.Output.Split('\n').Any(line => !string.IsNullOrWhiteSpace(line));
    }

    private static async Task<string?> RootParentDeviceAsync(CancellationToken cancellationToken)
    {
        var sourceResult = await InstallDiskProcess.CaptureAsync("findmnt", ["-n", "-o", "SOURCE", "/"], cancellationToken);
        var source = sourceResult.ExitCode == 0 ? sourceResult.Output.Trim() : string.Empty;
        if (string.IsNullOrWhiteSpace(source) || !await IsBlockDeviceAsync(source, cancellationToken))
        {
            return null;
        }

        var parentResult = await InstallDiskProcess.CaptureAsync("lsblk", ["-no", "PKNAME", source], cancellationToken);
        var parent = parentResult.ExitCode == 0 ? parentResult.Output.Split('\n').FirstOrDefault()?.Trim() : null;
        if (string.IsNullOrWhiteSpace(parent))
        {
            return null;
        }

        var realResult = await InstallDiskProcess.CaptureAsync("readlink", ["-f", "/dev/" + parent], cancellationToken);
        return realResult.ExitCode == 0 ? realResult.Output.Trim() : "/dev/" + parent;
    }

    private static string PartitionPath(string device, int index)
        => device + PartitionSuffix(device) + index.ToString(CultureInfo.InvariantCulture);

    private static string PartitionSuffix(string device)
    {
        var name = Path.GetFileName(device);
        return name.EndsWithNumber() ||
               NvmePartitionNamePattern().IsMatch(name) ||
               name.StartsWith("mmcblk", StringComparison.Ordinal) ||
               name.StartsWith("loop", StringComparison.Ordinal)
            ? "p"
            : string.Empty;
    }

    private static IReadOnlyList<KernelAddon> ReadManifestAddons(JsonElement manifest)
    {
        if (!manifest.TryGetProperty("addons", out var addons) || addons.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return [];
        }

        if (addons.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("kernel OTA manifest addons must be an array");
        }

        var result = new List<KernelAddon>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var addon in addons.EnumerateArray())
        {
            var key = RequiredJsonString(addon, "key");
            if (!KernelAddonKeyPattern().IsMatch(key))
            {
                throw new InvalidOperationException("kernel addon key is invalid: " + key);
            }

            if (!keys.Add(key))
            {
                throw new InvalidOperationException("duplicate kernel addon key: " + key);
            }

            var file = RequiredJsonString(addon, "file");
            TarSafety.ValidateMemberPath(file, "kernel addon file");
            if (!file.StartsWith("addons/", StringComparison.Ordinal) || !file.EndsWith(".erofs", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("kernel addon file must be under addons/ and end with .erofs: " + file);
            }

            var sha256 = RequiredJsonString(addon, "sha256");
            if (!IsSha256(sha256))
            {
                throw new InvalidOperationException("kernel addon " + key + " sha256 must be a SHA-256 hex digest");
            }

            var filesystem = RequiredJsonString(addon, "filesystem");
            if (filesystem != "erofs")
            {
                throw new InvalidOperationException("kernel addon " + key + " filesystem must be erofs");
            }

            var overlay = RequiredJsonString(addon, "overlay");
            if (overlay != "usr")
            {
                throw new InvalidOperationException("kernel addon " + key + " overlay must be usr");
            }

            result.Add(new KernelAddon(key, file, sha256.ToLowerInvariant()));
        }

        return result;
    }

    private string AddonList()
        => string.Join(',', _kernelAddons.Select(addon => addon.Key));

    private static string AddonEnvSuffix(string key)
        => key.ToUpperInvariant().Replace('.', '_').Replace('-', '_');

    private static string? JsonString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined
            ? value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString()
            : null;

    private static string RequiredJsonString(JsonElement element, string property)
        => JsonString(element, property) ?? throw new InvalidOperationException("manifest is missing required field: " + property);

    private static bool IsSafeKernelRelease(string value)
        => value.All(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '+' or '-');

    private static bool IsSha256(string value)
        => value.Length == 64 && value.All(Uri.IsHexDigit);

    private static bool IsLowerSha256(string value)
        => value.Length == 64 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string FirstToken(string value)
        => value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;

    private static async Task<string> Sha256HexAsync(string path, CancellationToken cancellationToken)
    {
        await using var input = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken)).ToLowerInvariant();
    }

    private static void RequireTools(IEnumerable<string> tools)
    {
        foreach (var tool in tools)
        {
            if (!PathHasExecutable(tool))
            {
                throw new InvalidOperationException("missing required tool: " + tool);
            }
        }
    }

    private static bool PathHasExecutable(string name)
    {
        if (name.Contains('/'))
        {
            return File.Exists(name);
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (File.Exists(Path.Combine(directory, name)))
            {
                return true;
            }
        }

        return false;
    }

    private static string FindHomeHarborRoot()
    {
        var current = AppContext.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(current))
        {
            if (File.Exists(Path.Combine(current, "src", "HomeHarbor.ImageBuilder", "HomeHarbor.ImageBuilder.csproj")) ||
                File.Exists(Path.Combine(current, "certs", "homeharbor-secure-boot.crt")))
            {
                return current;
            }

            var parent = Directory.GetParent(current)?.FullName;
            if (string.Equals(parent, current, StringComparison.Ordinal))
            {
                break;
            }

            current = parent ?? string.Empty;
        }

        return "/usr/lib/homeharbor";
    }

    private static void InstallFile(string source, string destination, UnixFileMode mode)
    {
        if (!File.Exists(source))
        {
            throw new FileNotFoundException("source file not found: " + source, source);
        }

        _ = Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(source, destination, overwrite: true);
        SetMode(destination, mode);
    }

    private static void WriteTextFile(string path, string content, UnixFileMode mode)
    {
        _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        SetMode(path, mode);
    }

    private static void SetMode(string path, UnixFileMode mode)
    {
        try
        {
            File.SetUnixFileMode(path, mode);
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or UnauthorizedAccessException or IOException)
        {
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [GeneratedRegex("^nvme.*n", RegexOptions.CultureInvariant)]
    private static partial Regex NvmePartitionNamePattern();

    [GeneratedRegex("^[a-z0-9._-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex KernelAddonKeyPattern();
}
