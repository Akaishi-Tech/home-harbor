using System.Collections.ObjectModel;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Diagnostics;
using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HomeHarbor.Tooling;
using Terminal.Gui.App;
using Terminal.Gui.Configuration;
using Terminal.Gui.Drawing;
using Terminal.Gui.Drivers;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using TuiAttribute = Terminal.Gui.Drawing.Attribute;

[assembly: InternalsVisibleTo("HomeHarbor.Tests")]

try
{
    return await InstallerProgram.RunAsync(args, CancellationToken.None);
}
catch (InstallerCancelledException)
{
    Console.Error.WriteLine("Install cancelled.");
    return 2;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    return ex is ArgumentException ? 2 : 1;
}

internal static partial class InstallerProgram
{
    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        var parseResult = CreateRootCommand().Parse(args);
        var exitCode = await parseResult.InvokeAsync(
            new InvocationConfiguration { EnableDefaultExceptionHandler = false },
            cancellationToken);
        return parseResult.Errors.Count > 0 && exitCode != 0 ? 2 : exitCode;
    }

    private static Argument<string> RequiredArgument(string name, string description)
        => new(name) { Description = description };

    private static Argument<string> OptionalArgument(string name, string defaultValue, string description)
        => new(name)
        {
            Arity = ArgumentArity.ZeroOrOne,
            DefaultValueFactory = _ => defaultValue,
            Description = description
        };

    private static Argument<string?> OptionalNullableArgument(string name, string description)
        => new(name)
        {
            Arity = ArgumentArity.ZeroOrOne,
            Description = description
        };
}

internal sealed record InstallerOptions(
    string Mode,
    string PayloadDirectory,
    string? SystemOta,
    IReadOnlyList<string> ExternalPayloadDirectories,
    string PublicKey,
    string StableChannelUrl,
    string DailyChannelUrl)
{
    public static InstallerOptions Parse(string[] args)
    {
        var commandOptions = CreateCommandOptions();
        var command = new RootCommand();
        commandOptions.AddTo(command);
        var parseResult = command.Parse(args);
        return parseResult.Errors.Count > 0
            ? throw new ArgumentException(string.Join(Environment.NewLine, parseResult.Errors.Select(error => error.Message)))
            : FromParseResult(parseResult, commandOptions);
    }

    public static CommandOptions CreateCommandOptions()
    {
        var modeOption = StringOption("--mode", "tiny", "Installer mode: full or tiny.");
        modeOption.Validators.Add(result =>
        {
            var mode = result.GetValueOrDefault<string>();
            if (!string.Equals(mode, "full", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(mode, "tiny", StringComparison.OrdinalIgnoreCase))
            {
                result.AddError("--mode must be full or tiny");
            }
        });

        return new CommandOptions(
            modeOption,
            StringOption("--payload-dir", "/opt/homeharbor-installer/payloads", "Bundled payload directory."),
            NullableStringOption("--system-ota", "System OTA path."),
            new Option<string[]>("--external-payload-dir")
            {
                Arity = ArgumentArity.OneOrMore,
                Description = "Additional removable payload directory."
            },
            StringOption("--public-key", "/etc/homeharbor/release.pub.pem", "Release public key."),
            StringOption("--stable-channel-url", "https://github.com/akaishi-tech/home-harbor/releases/latest/download/channel-stable.json", "Stable channel URL."),
            StringOption("--daily-channel-url", "https://github.com/akaishi-tech/home-harbor/releases/download/daily/channel-daily.json", "Daily channel URL."));
    }

    public static InstallerOptions FromParseResult(ParseResult parseResult, CommandOptions options)
    {
        var mode = parseResult.GetValue(options.Mode)!;
        var payloadDirectory = parseResult.GetValue(options.PayloadDirectory)!;
        var externalPayloadDirectories = (parseResult.GetValue(options.ExternalPayloadDirectories) ?? []).ToList();
        var hasExternalPayloadDirectories = parseResult.GetResult(options.ExternalPayloadDirectories) is not null;
        externalPayloadDirectories = hasExternalPayloadDirectories
            ? [.. CleanPathList(new[] { payloadDirectory }.Concat(externalPayloadDirectories))]
            : [.. DefaultExternalPayloadDirectories(payloadDirectory)];

        return new InstallerOptions(
            mode.ToLowerInvariant(),
            payloadDirectory,
            parseResult.GetValue(options.SystemOta),
            externalPayloadDirectories,
            parseResult.GetValue(options.PublicKey)!,
            parseResult.GetValue(options.StableChannelUrl)!,
            parseResult.GetValue(options.DailyChannelUrl)!);
    }

    private static Option<string> StringOption(string name, string defaultValue, string description)
        => new(name)
        {
            DefaultValueFactory = _ => defaultValue,
            Description = description
        };

    private static Option<string?> NullableStringOption(string name, string description)
        => new(name) { Description = description };

    public sealed record CommandOptions(
        Option<string> Mode,
        Option<string> PayloadDirectory,
        Option<string?> SystemOta,
        Option<string[]> ExternalPayloadDirectories,
        Option<string> PublicKey,
        Option<string> StableChannelUrl,
        Option<string> DailyChannelUrl)
    {
        public void AddTo(Command command)
        {
            command.Options.Add(Mode);
            command.Options.Add(PayloadDirectory);
            command.Options.Add(SystemOta);
            command.Options.Add(ExternalPayloadDirectories);
            command.Options.Add(PublicKey);
            command.Options.Add(StableChannelUrl);
            command.Options.Add(DailyChannelUrl);
        }
    }

    private static IReadOnlyList<string> DefaultExternalPayloadDirectories(string payloadDirectory)
        => CleanPathList([
            payloadDirectory,
            "/run/archiso/bootmnt",
            "/run/media",
            "/media",
            "/mnt",
            Directory.GetCurrentDirectory()
        ]);

    private static IReadOnlyList<string> CleanPathList(IEnumerable<string> paths)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            var fullPath = Path.GetFullPath(path.Trim());
            if (seen.Add(fullPath))
            {
                result.Add(fullPath);
            }
        }

        return result;
    }
}

internal static class Installer
{
    public static async Task<int> RunAsync(InstallerOptions options)
    {
        var ui = Environment.GetEnvironmentVariable("HOMEHARBOR_INSTALLER_UI");
        if (!string.IsNullOrWhiteSpace(ui))
        {
            if (!string.Equals(ui, "tui", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(ui, "auto", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("HOMEHARBOR_INSTALLER_UI must be 'auto' or 'tui'.");
            }
        }

        if (!TerminalGuiInstallerUi.CanUse)
        {
            Console.Error.WriteLine("The HomeHarbor installer requires an interactive TUI console.");
            return 1;
        }

        try
        {
            return await new TerminalGuiInstallerUi(options).RunAsync();
        }
        catch (Exception ex)
        {
            TerminalGuiInstallerUi.WriteStartupFailure(ex);
            throw;
        }
    }

    internal static async Task<int> RunInstallDiskAsync(
        InstallerOptions options,
        DiskInfo disk,
        InstallerAssets assets,
        string expected,
        Action<string>? output = null)
    {
        _ = options;
        var args = BuildInstallDiskArgs(disk, assets, expected);
        try
        {
            return await InstallDiskCommand.RunAsync(
                [.. args],
                output,
                allowVerifiedArchisoRoot: string.Equals(options.Mode, "full", StringComparison.Ordinal));
        }
        catch (Exception ex)
        {
            (output ?? Console.Error.Write).Invoke(ex.Message + Environment.NewLine);
            return ex is ArgumentException ? 2 : 1;
        }
    }

    internal static async Task<InstallerDryRunResult> RunDryRunAsync(
        InstallerOptions options,
        DiskInfo disk,
        InstallerAssets assets,
        string expected)
    {
        _ = options;
        var args = BuildInstallDiskArgs(disk, assets, expected, dryRun: true);
        var output = new StringBuilder();
        try
        {
            var exitCode = await InstallDiskCommand.RunAsync(
                [.. args],
                chunk => output.Append(chunk),
                allowVerifiedArchisoRoot: string.Equals(options.Mode, "full", StringComparison.Ordinal));
            return InstallerDryRunResult.FromProcess(exitCode, output.ToString());
        }
        catch (Exception ex)
        {
            _ = output.AppendLine(ex.Message);
            return InstallerDryRunResult.FromProcess(ex is ArgumentException ? 2 : 1, output.ToString());
        }
    }

    internal static List<string> BuildInstallDiskArgs(
        DiskInfo disk,
        InstallerAssets assets,
        string expected,
        bool dryRun = false)
    {
        var args = new List<string>
        {
            "--target", disk.Path,
            "--system-ota", assets.SystemOta,
            "--public-key", assets.PublicKey,
            "--confirm", expected,
            "--expected-size-bytes", disk.SizeBytes.ToString(CultureInfo.InvariantCulture),
            "--expected-resolved-path", disk.ResolvedPath ?? disk.Path
        };
        if (!string.IsNullOrWhiteSpace(disk.Serial))
        {
            args.Add("--expected-serial");
            args.Add(disk.Serial);
        }
        if (!string.IsNullOrWhiteSpace(disk.Wwn))
        {
            args.Add("--expected-wwn");
            args.Add(disk.Wwn);
        }
        if (!dryRun)
        {
            args.Add("--yes");
        }

        if (!string.IsNullOrWhiteSpace(assets.ChannelFile))
        {
            args.Add("--channel-file");
            args.Add(assets.ChannelFile);
        }

        if (!string.IsNullOrWhiteSpace(assets.KernelOta))
        {
            args.Add("--kernel-ota");
            args.Add(assets.KernelOta);
        }

        if (!string.IsNullOrWhiteSpace(assets.ManifestFile))
        {
            args.Add("--system-manifest");
            args.Add(assets.ManifestFile);
        }

        if (dryRun)
        {
            args.Add("--dry-run");
        }

        return args;
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.#} {units[unit]}";
    }

}

internal enum InstallerMainAction
{
    Install,
    SystemStatus,
    NetworkSettings,
    Diagnostics,
    RescueShell,
    EnableMouse,
    Quit
}

internal enum InstallerNetworkAction
{
    Wired,
    Wireless,
    Disconnect,
    Refresh,
    Back
}

internal enum InstallerIpMode
{
    Dhcp,
    Static
}

internal enum WifiNetworkChoiceKind
{
    AccessPoint,
    Hidden
}

internal sealed record WifiNetworkChoice(WifiNetworkChoiceKind Kind, WifiAccessPoint? AccessPoint);

internal sealed record DiskInfo(string Path, long SizeBytes, string Model)
{
    public string? Serial { get; init; }
    public string? Wwn { get; init; }
    public string? Transport { get; init; }
    public string? ResolvedPath { get; init; }
    public IReadOnlyList<string> Mountpoints { get; init; } = [];

    public bool HasMounts => Mountpoints.Count > 0;

    public static async Task<IReadOnlyList<DiskInfo>> ListAsync()
    {
        var (ExitCode, Output) = await ProcessRunner.CaptureAsync("lsblk", ["-J", "-b", "-o", "PATH,SIZE,MODEL,TYPE,SERIAL,WWN,TRAN,MOUNTPOINTS"]);
        return ExitCode != 0 ? throw new InvalidOperationException(Output) : ParseListJson(Output);
    }

    internal static IReadOnlyList<DiskInfo> ParseListJson(string json)
    {
        var disks = new List<DiskInfo>();
        using var doc = JsonDocument.Parse(json);
        foreach (var device in doc.RootElement.GetProperty("blockdevices").EnumerateArray())
        {
            if (!string.Equals(GetString(device, "type"), "disk", StringComparison.Ordinal))
            {
                continue;
            }

            var path = GetString(device, "path");
            var size = GetInt64(device, "size");
            var model = GetString(device, "model");
            disks.Add(new DiskInfo(path, size, string.IsNullOrWhiteSpace(model) ? "disk" : model)
            {
                Serial = Normalize(GetString(device, "serial")),
                Wwn = Normalize(GetString(device, "wwn")),
                Transport = Normalize(GetString(device, "tran")),
                ResolvedPath = path,
                Mountpoints = GetMountpoints(device)
            });
        }

        return disks;
    }

    private static string GetString(JsonElement element, string property)
    {
        return !element.TryGetProperty(property, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
            ? string.Empty
            : value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : value.ToString();
    }

    private static long GetInt64(JsonElement element, string property)
    {
        return !element.TryGetProperty(property, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
            ? 0
            : value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)
            ? number
            : long.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;
    }

    private static IReadOnlyList<string> GetMountpoints(JsonElement element)
    {
        if (!element.TryGetProperty("mountpoints", out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return [];
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            var single = Normalize(value.GetString());
            return single is null ? [] : [single];
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var mountpoints = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            var mountpoint = item.ValueKind == JsonValueKind.String ? Normalize(item.GetString()) : null;
            if (mountpoint is not null)
            {
                mountpoints.Add(mountpoint);
            }
        }

        return mountpoints;
    }

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

internal sealed record InstallerAssets(
    string Version,
    string Channel,
    string SystemOta,
    string PublicKey,
    string? ChannelFile,
    string? ManifestFile = null,
    string? KernelOta = null)
{
    private const string DefaultKernelChannel = "generic";
    internal const long MaxChannelMetadataBytes = 1024 * 1024;
    internal const long MaxOtaBundleBytes = 4L * 1024 * 1024 * 1024;
    private static readonly TimeSpan ChannelDownloadTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan OtaDownloadTimeout = TimeSpan.FromHours(2);

    public static InstallerAssets FromPayloadDirectory(
        string payloadDirectory,
        string publicKey,
        bool allowAdjacentDevelopmentKey = false)
    {
        if (!Directory.Exists(payloadDirectory))
        {
            throw new DirectoryNotFoundException(payloadDirectory);
        }

        var systemOta = SingleFile(payloadDirectory, "homeharbor-system-ota-*.tar.gz");
        var key = ResolveReleasePublicKey(payloadDirectory, publicKey, allowAdjacentDevelopmentKey);
        var version = VersionFromOta(systemOta);
        var manifestFile = SystemOtaManifestSidecar(payloadDirectory);
        var manifestChannel = manifestFile is not null
            ? ChannelFromManifestFile(manifestFile)
            : ChannelFromSystemOta(systemOta);
        var channel = FindChannelMetadata(payloadDirectory, manifestChannel);
        var assetChannel = channel is null
            ? manifestChannel
            : ChannelFromMetadata(channel, version, manifestChannel);
        var kernelOta = ResolveKernelOta(payloadDirectory, channel);
        return new InstallerAssets(version, assetChannel, systemOta, key, channel, manifestFile, kernelOta);
    }

    public static bool TryFromPayloadDirectory(
        string payloadDirectory,
        string publicKey,
        out InstallerAssets? assets,
        out string? error)
    {
        try
        {
            assets = FromPayloadDirectory(payloadDirectory, publicKey);
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or IOException or InvalidOperationException or UnauthorizedAccessException or JsonException)
        {
            assets = null;
            error = ex.Message;
            return false;
        }
    }

    public static InstallerAssets FromSystemOtaFile(
        string systemOta,
        string publicKey,
        bool allowAdjacentDevelopmentKey = false)
    {
        if (string.IsNullOrWhiteSpace(systemOta))
        {
            throw new ArgumentException("System OTA path must not be empty.", nameof(systemOta));
        }

        var fullPath = Path.GetFullPath(systemOta);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("System OTA not found: " + fullPath, fullPath);
        }

        if (!IsSystemOtaFileName(fullPath))
        {
            throw new ArgumentException("System OTA file must be named homeharbor-system-ota-*.tar.gz: " + fullPath);
        }

        var directory = Path.GetDirectoryName(fullPath) ?? Directory.GetCurrentDirectory();
        var key = ResolveReleasePublicKey(directory, publicKey, allowAdjacentDevelopmentKey);
        var version = VersionFromOta(fullPath);
        var manifestFile = SystemOtaManifestSidecar(directory);
        var manifestChannel = manifestFile is not null
            ? ChannelFromManifestFile(manifestFile)
            : ChannelFromSystemOta(fullPath);
        var channel = FindChannelMetadata(directory, manifestChannel);
        var assetChannel = channel is null
            ? manifestChannel
            : ChannelFromMetadata(channel, version, manifestChannel);
        var kernelOta = ResolveKernelOta(directory, channel);
        return new InstallerAssets(version, assetChannel, fullPath, key, channel, manifestFile, kernelOta);
    }

    private static string ResolveReleasePublicKey(
        string payloadDirectory,
        string trustedPublicKey,
        bool allowAdjacentDevelopmentKey)
    {
        if (string.IsNullOrWhiteSpace(trustedPublicKey))
        {
            throw new ArgumentException("Trusted release public key path must not be empty.", nameof(trustedPublicKey));
        }

        var trusted = Path.GetFullPath(trustedPublicKey);
        var adjacent = Path.Combine(Path.GetFullPath(payloadDirectory), "release.pub.pem");
        if (!allowAdjacentDevelopmentKey || !File.Exists(adjacent))
        {
            return trusted;
        }

        Console.Error.WriteLine(
            "WARNING: using an adjacent development release key; never enable this for removable or downloaded payloads: " + adjacent);
        return adjacent;
    }

    public static IReadOnlyList<string> FindExternalSystemOtas(IEnumerable<string> roots)
    {
        const int maxDepth = 4;
        var results = new SortedSet<string>(StringComparer.Ordinal);
        var scanned = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<(string Directory, int Depth)>();

        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            string fullRoot;
            try
            {
                fullRoot = Path.GetFullPath(root.Trim());
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
            {
                continue;
            }

            if (File.Exists(fullRoot))
            {
                if (IsSystemOtaFileName(fullRoot))
                {
                    _ = results.Add(fullRoot);
                }

                continue;
            }

            if (Directory.Exists(fullRoot) && scanned.Add(fullRoot))
            {
                queue.Enqueue((fullRoot, 0));
            }
        }

        while (queue.Count > 0)
        {
            var (directory, depth) = queue.Dequeue();
            try
            {
                foreach (var file in Directory.EnumerateFiles(directory, "homeharbor-system-ota-*.tar.gz"))
                {
                    if (IsSystemOtaFileName(file))
                    {
                        _ = results.Add(Path.GetFullPath(file));
                    }
                }

                if (depth >= maxDepth)
                {
                    continue;
                }

                foreach (var child in Directory.EnumerateDirectories(directory))
                {
                    var fullChild = Path.GetFullPath(child);
                    if (scanned.Add(fullChild))
                    {
                        queue.Enqueue((fullChild, depth + 1));
                    }
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or DirectoryNotFoundException or NotSupportedException)
            {
                continue;
            }
        }

        return results.ToArray();
    }

    public static async Task<InstallerAssets> DownloadLatestAsync(
        string channel,
        string channelUrl,
        string publicKey,
        Action<string>? reportStatus = null,
        CancellationToken cancellationToken = default)
    {
        using var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false
        };
        using var http = new HttpClient(handler)
        {
            Timeout = System.Threading.Timeout.InfiniteTimeSpan
        };
        return await DownloadLatestAsync(channel, channelUrl, publicKey, http, reportStatus, cancellationToken);
    }

    internal static async Task<InstallerAssets> DownloadLatestAsync(
        string channel,
        string channelUrl,
        string publicKey,
        HttpClient http,
        Action<string>? reportStatus = null,
        CancellationToken cancellationToken = default)
    {
        channel = ReleaseChannel.Require(channel, "installer channel");
        var work = Path.Combine(Path.GetTempPath(), "homeharbor-installer-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(work);
        try
        {
            var channelFile = Path.Combine(work, "channel-" + channel + ".json");
            reportStatus?.Invoke("Downloading " + channel + " channel metadata.");
            await DownloadFileAsync(
                http,
                channelUrl,
                channelFile,
                MaxChannelMetadataBytes,
                ChannelDownloadTimeout,
                cancellationToken);

            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(channelFile, cancellationToken));
            var root = doc.RootElement;
            var metadataChannel = ReleaseChannel.Require(GetString(root, "channel"), "channel metadata channel");
            if (!string.Equals(metadataChannel, channel, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Channel metadata is for {metadataChannel}, expected {channel}.");
            }

            var version = GetRequiredString(root, "currentVersion", "current version");
            if (!SecurityGuards.IsSafeVersion(version))
            {
                throw new InvalidOperationException("Channel metadata currentVersion contains unsafe characters.");
            }

            var system = root.GetProperty("systemOta").GetProperty("bundle");
            var systemUrl = GetRequiredString(system, "url", "system OTA bundle URL");
            var systemPath = Path.Combine(
                work,
                SafeBundleFileName(systemUrl, "homeharbor-system-ota-", ".tar.gz", "system OTA bundle URL"));
            var systemSha = RequiredSha256(system, "system OTA bundle");

            reportStatus?.Invoke("Downloading system OTA payload.");
            await DownloadFileAsync(http, systemUrl, systemPath, MaxOtaBundleBytes, OtaDownloadTimeout, cancellationToken);
            reportStatus?.Invoke("Verifying system OTA SHA-256.");
            await VerifySha256Async(systemPath, systemSha, cancellationToken);

            var kernel = root.GetProperty("kernelChannels").GetProperty(DefaultKernelChannel).GetProperty("bundle");
            var kernelUrl = GetRequiredString(kernel, "url", DefaultKernelChannel + " kernel bundle URL");
            var kernelPath = Path.Combine(
                work,
                SafeBundleFileName(
                    kernelUrl,
                    "homeharbor-kernel-" + DefaultKernelChannel + "-ota-",
                    ".tar.gz",
                    DefaultKernelChannel + " kernel bundle URL"));
            var kernelSha = RequiredSha256(kernel, DefaultKernelChannel + " kernel bundle");
            if (string.Equals(systemPath, kernelPath, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Channel metadata resolves system and kernel bundles to the same local file.");
            }

            reportStatus?.Invoke("Downloading " + DefaultKernelChannel + " kernel OTA payload.");
            await DownloadFileAsync(http, kernelUrl, kernelPath, MaxOtaBundleBytes, OtaDownloadTimeout, cancellationToken);
            reportStatus?.Invoke("Verifying " + DefaultKernelChannel + " kernel OTA SHA-256.");
            await VerifySha256Async(kernelPath, kernelSha, cancellationToken);

            return new InstallerAssets(version, channel, systemPath, publicKey, channelFile, KernelOta: kernelPath);
        }
        catch
        {
            TryDeleteDirectory(work);
            throw;
        }
    }

    private static string? ResolveKernelOta(string directory, string? channelFile)
    {
        return !string.IsNullOrWhiteSpace(channelFile)
            ? ResolveKernelOtaFromChannelMetadata(directory, channelFile)
            : OptionalSingleFile(directory, "homeharbor-kernel-" + DefaultKernelChannel + "-ota-*.tar.gz");
    }

    private static string ResolveKernelOtaFromChannelMetadata(string directory, string channelFile)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(channelFile));
        var root = doc.RootElement;
        if (!root.TryGetProperty("kernelChannels", out var channels) ||
            !channels.TryGetProperty(DefaultKernelChannel, out var kernelChannel) ||
            !kernelChannel.TryGetProperty("bundle", out var bundle))
        {
            throw new InvalidOperationException("Channel metadata does not include a " + DefaultKernelChannel + " kernel bundle.");
        }

        var bundleFile = GetString(bundle, "file");
        if (string.IsNullOrWhiteSpace(bundleFile))
        {
            var bundleUrl = GetString(bundle, "url");
            bundleFile = string.IsNullOrWhiteSpace(bundleUrl)
                ? null
                : Path.GetFileName(new Uri(bundleUrl).LocalPath);
        }

        if (string.IsNullOrWhiteSpace(bundleFile))
        {
            throw new InvalidOperationException("Channel metadata does not include a local " + DefaultKernelChannel + " kernel bundle file.");
        }

        var kernelOta = ResolveExistingBundlePath(directory, channelFile, bundleFile) ?? throw new FileNotFoundException("Kernel OTA from channel metadata was not found near the payload directory: " + bundleFile);
        VerifySha256(kernelOta, GetString(bundle, "sha256"));
        return kernelOta;
    }

    private static string? ResolveExistingBundlePath(string directory, string channelFile, string bundleFile)
    {
        var channelDirectory = Path.GetDirectoryName(channelFile) ?? directory;
        var candidates = new List<string>();
        if (Path.IsPathRooted(bundleFile))
        {
            candidates.Add(bundleFile);
        }
        else
        {
            candidates.Add(Path.Combine(channelDirectory, bundleFile));
            candidates.Add(Path.Combine(directory, bundleFile));
            var parent = Directory.GetParent(channelDirectory)?.FullName;
            if (parent is not null)
            {
                candidates.Add(Path.Combine(parent, bundleFile));
            }

            var leaf = Path.GetFileName(bundleFile);
            if (!string.IsNullOrWhiteSpace(leaf))
            {
                candidates.Add(Path.Combine(channelDirectory, leaf));
                candidates.Add(Path.Combine(directory, leaf));
            }
        }

        return candidates
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.Ordinal)
            .FirstOrDefault(File.Exists);
    }

    private static string ChannelFromMetadata(string channelFile, string expectedVersion, string expectedChannel)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(channelFile));
        var root = doc.RootElement;
        var channel = ReleaseChannel.Require(GetString(root, "channel"), "channel metadata channel");
        var version = GetString(root, "currentVersion");
        return !string.IsNullOrWhiteSpace(version) && !string.Equals(version, expectedVersion, StringComparison.Ordinal)
            ? throw new InvalidOperationException($"Channel metadata version {version} does not match OTA version {expectedVersion}.")
            : !string.Equals(channel, expectedChannel, StringComparison.Ordinal)
            ? throw new InvalidOperationException($"Channel metadata {channel} does not match OTA channel {expectedChannel}.")
            : channel;
    }

    private static string? FindChannelMetadata(string directory, string channel)
    {
        var exact = Path.Combine(directory, "channel-" + channel + ".json");
        return File.Exists(exact)
            ? exact
            : Directory.EnumerateFiles(directory, "channel-*.json").OrderBy(p => p, StringComparer.Ordinal).FirstOrDefault();
    }

    private static string ChannelFromSystemOta(string systemOta)
    {
        using var file = File.OpenRead(systemOta);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new TarReader(gzip);
        TarEntry? entry;
        while ((entry = reader.GetNextEntry()) is not null)
        {
            if (!entry.Name.EndsWith("/manifest.json", StringComparison.Ordinal) ||
                !entry.Name.StartsWith("homeharbor-system-ota-", StringComparison.Ordinal) ||
                entry.DataStream is null)
            {
                continue;
            }

            using var doc = JsonDocument.Parse(entry.DataStream);
            return ReleaseChannel.Require(GetString(doc.RootElement, "channel"), "system OTA channel");
        }

        throw new InvalidOperationException("System OTA bundle does not contain a system manifest.json.");
    }

    private static string? SystemOtaManifestSidecar(string directory)
    {
        var sidecarManifest = Path.Combine(directory, "system-ota-manifest.json");
        return File.Exists(sidecarManifest) ? sidecarManifest : null;
    }

    private static string ChannelFromManifestFile(string manifest)
    {
        using var file = File.OpenRead(manifest);
        using var doc = JsonDocument.Parse(file);
        return ReleaseChannel.Require(GetString(doc.RootElement, "channel"), "system OTA channel");
    }

    private static string? GetString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined
            ? value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString()
            : null;

    private static string GetRequiredString(JsonElement element, string property, string label)
        => GetString(element, property) ?? throw new InvalidOperationException("Channel metadata did not include " + label + ".");

    private static string SingleFile(string directory, string pattern)
    {
        var matches = Directory.EnumerateFiles(directory, pattern).OrderBy(p => p, StringComparer.Ordinal).ToArray();
        return matches.Length == 1
            ? matches[0]
            : throw new InvalidOperationException($"Expected exactly one {pattern} in {directory}, found {matches.Length}.");
    }

    private static string? OptionalSingleFile(string directory, string pattern)
    {
        var matches = Directory.EnumerateFiles(directory, pattern).OrderBy(p => p, StringComparer.Ordinal).ToArray();
        return matches.Length switch
        {
            0 => null,
            1 => matches[0],
            _ => throw new InvalidOperationException($"Expected at most one {pattern} in {directory}, found {matches.Length}.")
        };
    }

    private static string VersionFromOta(string path)
    {
        var name = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(path));
        const string prefix = "homeharbor-system-ota-";
        return name.StartsWith(prefix, StringComparison.Ordinal) ? name[prefix.Length..] : "unknown";
    }

    private static bool IsSystemOtaFileName(string path)
    {
        var name = Path.GetFileName(path);
        const string prefix = "homeharbor-system-ota-";
        const string suffix = ".tar.gz";
        return name.Length > prefix.Length + suffix.Length &&
               name.StartsWith(prefix, StringComparison.Ordinal) &&
               name.EndsWith(suffix, StringComparison.Ordinal);
    }

    internal static async Task DownloadFileAsync(
        HttpClient http,
        string url,
        string path,
        long maxBytes,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);

        var current = RequireHttpsUri(url, "download URL");
        var destination = Path.GetFullPath(path);
        if (File.Exists(destination) || Directory.Exists(destination))
        {
            throw new IOException("download destination already exists: " + destination);
        }

        _ = Directory.CreateDirectory(Path.GetDirectoryName(destination) ?? ".");
        var temp = destination + ".part." + Guid.NewGuid().ToString("N");
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            for (var redirect = 0; redirect <= 5; redirect++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, current);
                using var response = await http.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    timeoutSource.Token);
                if (response.RequestMessage?.RequestUri is { } effectiveUri)
                {
                    _ = RequireHttpsUri(effectiveUri, "effective download URL");
                }

                if (IsRedirect(response.StatusCode))
                {
                    if (redirect == 5)
                    {
                        throw new HttpRequestException("download exceeded the maximum of five redirects");
                    }

                    var location = response.Headers.Location
                        ?? throw new HttpRequestException("download redirect did not include a Location header");
                    current = RequireHttpsUri(
                        location.IsAbsoluteUri ? location : new Uri(current, location),
                        "download redirect URL");
                    continue;
                }

                _ = response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength is { } contentLength && contentLength > maxBytes)
                {
                    throw new InvalidOperationException($"download Content-Length {contentLength} exceeds limit {maxBytes}");
                }

                await using var input = await response.Content.ReadAsStreamAsync(timeoutSource.Token);
                await using var output = new FileStream(temp, new FileStreamOptions
                {
                    Access = FileAccess.Write,
                    Mode = FileMode.CreateNew,
                    Share = FileShare.None,
                    Options = FileOptions.WriteThrough
                });
                var buffer = new byte[1024 * 1024];
                long copied = 0;
                int read;
                while ((read = await input.ReadAsync(buffer.AsMemory(), timeoutSource.Token)) > 0)
                {
                    if (copied > maxBytes - read)
                    {
                        throw new InvalidOperationException($"download exceeded limit {maxBytes}");
                    }

                    await output.WriteAsync(buffer.AsMemory(0, read), timeoutSource.Token);
                    copied += read;
                }

                await output.FlushAsync(timeoutSource.Token);
                output.Flush(flushToDisk: true);
                File.Move(temp, destination, overwrite: false);
                return;
            }
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("download timed out after " + timeout, ex);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    private static async Task VerifySha256Async(string path, string? expected, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(expected))
        {
            throw new InvalidOperationException("Channel metadata did not include sha256 for " + path);
        }

        await using var input = File.OpenRead(path);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken)).ToLowerInvariant();
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"SHA-256 mismatch for {path}: expected {expected}, got {actual}");
        }
    }

    private static void VerifySha256(string path, string? expected)
    {
        if (string.IsNullOrWhiteSpace(expected))
        {
            throw new InvalidOperationException("Channel metadata did not include sha256 for " + path);
        }

        using var input = File.OpenRead(path);
        var actual = Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"SHA-256 mismatch for {path}: expected {expected}, got {actual}");
        }
    }

    private static string SafeBundleFileName(
        string url,
        string requiredPrefix,
        string requiredSuffix,
        string label)
    {
        var uri = RequireHttpsUri(url, label);
        var fileName = Path.GetFileName(uri.LocalPath);
        if (string.IsNullOrWhiteSpace(fileName) || fileName.Any(char.IsControl) ||
            !fileName.StartsWith(requiredPrefix, StringComparison.Ordinal) ||
            !fileName.EndsWith(requiredSuffix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(label + " has an unsafe or unexpected file name");
        }

        var version = fileName[requiredPrefix.Length..^requiredSuffix.Length];
        if (!SecurityGuards.IsSafeVersion(version))
        {
            throw new InvalidOperationException(label + " version contains unsafe characters");
        }

        return fileName;
    }

    private static string RequiredSha256(JsonElement bundle, string label)
    {
        var value = GetRequiredString(bundle, "sha256", label + " SHA-256");
        return value.Length == 64 && value.All(Uri.IsHexDigit)
            ? value.ToLowerInvariant()
            : throw new InvalidOperationException(label + " SHA-256 must be 64 hexadecimal characters");
    }

    private static Uri RequireHttpsUri(string value, string label)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri)
            ? RequireHttpsUri(uri, label)
            : throw new InvalidOperationException(label + " must be an absolute HTTPS URL");

    private static Uri RequireHttpsUri(Uri uri, string label)
        => string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
           !string.IsNullOrWhiteSpace(uri.Host) && string.IsNullOrEmpty(uri.UserInfo)
            ? uri
            : throw new InvalidOperationException(label + " must use HTTPS without embedded credentials");

    private static bool IsRedirect(HttpStatusCode statusCode)
        => statusCode is HttpStatusCode.MovedPermanently or
            HttpStatusCode.Redirect or
            HttpStatusCode.RedirectMethod or
            HttpStatusCode.TemporaryRedirect or
            HttpStatusCode.PermanentRedirect;

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

internal sealed record InstallSummary(
    DiskInfo Disk,
    string Mode,
    string Channel,
    InstallerAssets Assets,
    InstallerDryRunResult DryRun,
    string LogPath);

internal sealed record PayloadScan(InstallerAssets? Embedded, string? EmbeddedError, IReadOnlyList<string> ExternalSystemOtas);

internal enum PayloadChoiceKind
{
    Embedded,
    External,
    Manual
}

internal sealed record PayloadChoice(PayloadChoiceKind Kind, string? SystemOta, InstallerAssets? Assets);

internal sealed record InstallerDryRunResult(int ExitCode, string Output, IReadOnlyDictionary<string, string> Values)
{
    public bool Succeeded => ExitCode == 0;

    public static InstallerDryRunResult FromProcess(int exitCode, string output)
        => new(exitCode, output, ParseValues(output));

    public string FormatForConfirmation()
    {
        if (!Succeeded)
        {
            return BuildFailureMessage();
        }

        var lines = new List<string>
        {
            "Preflight validation passed.",
            "",
            "Dry-run plan",
            $"Target:              {ValueOrNone("target")}",
            $"Version:             {ValueOrNone("version")}",
            $"Channel:             {ValueOrNone("channel")}",
            $"Boot mode:           {ValueOrNone("bootMode")}",
            $"System OTA:          {ValueOrNone("systemOta")}",
            $"Disk size:           {FormatByteValue("diskSizeBytes")}",
            $"Minimum disk size:   {FormatByteValue("minimumBytes")}",
            $"Install work area:   {ValueOrNone("installWorkMinMiB")} MiB",
            $"MOK enrollment:      {ValueOrNone("willEnrollMok")}",
            $"Partitions to write: {ValueOrNone("willWritePartitions")}"
        };

        var kernelOta = ValueOrNone("kernelOta");
        if (kernelOta != "none")
        {
            lines.Insert(8, $"Kernel OTA:          {kernelOta}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    public string BuildFailureMessage()
        => string.Join(Environment.NewLine, [
            "Preflight validation failed with exit code " + ExitCode + ".",
            "",
            string.IsNullOrWhiteSpace(Output)
                ? "No dry-run output was captured."
                : Output.Trim()
        ]);

    private string ValueOrNone(string key)
        => Values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : "none";

    private string FormatByteValue(string key)
    {
        return Values.TryGetValue(key, out var value) &&
            long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bytes)
            ? TerminalGuiInstallerUi.FormatBytesForDisplay(bytes) + " (" + bytes.ToString(CultureInfo.InvariantCulture) + " bytes)"
            : "none";
    }

    private static IReadOnlyDictionary<string, string> ParseValues(string output)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || !line.Contains('=', StringComparison.Ordinal))
            {
                continue;
            }

            var separator = line.IndexOf('=');
            var key = line[..separator].Trim();
            if (key.Length == 0 || key.Any(c => !char.IsLetterOrDigit(c)))
            {
                continue;
            }

            values[key] = line[(separator + 1)..].Trim();
        }

        return new ReadOnlyDictionary<string, string>(values);
    }
}

internal sealed record HostSecurityState(bool Tpm2Available, bool SecureBootEnabled)
{
    public static HostSecurityState Detect()
        => new(Tpm2Available: DetectTpm2(), SecureBootEnabled: DetectSecureBootEnabled());

    public string BuildSummary()
        => string.Join(Environment.NewLine, [
            "TPM and boot security",
            "",
            "TPM2 device: " + (Tpm2Available ? "available" : "not detected"),
            "Secure Boot: " + (SecureBootEnabled ? "enabled" : "disabled or not detected"),
            "",
            Tpm2Available
                ? "TPM2 hardware is visible to the live installer."
                : "TPM2 hardware was not detected by the live installer.",
            SecureBootEnabled
                ? "UEFI Secure Boot is enabled."
                : "UEFI Secure Boot is disabled or not reported."
        ]);

    private static bool DetectTpm2()
        => File.Exists("/dev/tpmrm0") ||
           File.Exists("/dev/tpm0") ||
           Directory.Exists("/sys/class/tpm/tpm0");

    private static bool DetectSecureBootEnabled()
    {
        if (!Directory.Exists("/sys/firmware/efi/efivars"))
        {
            return false;
        }

        foreach (var path in Directory.EnumerateFiles("/sys/firmware/efi/efivars", "SecureBoot-*", SearchOption.TopDirectoryOnly))
        {
            try
            {
                using var stream = File.OpenRead(path);
                if (stream.Length < 5)
                {
                    continue;
                }

                stream.Position = 4;
                return stream.ReadByte() == 1;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
        }

        return false;
    }
}

internal sealed record NetworkDevice(string Name, string Type, string State, string Connection)
{
    public bool IsEthernet => string.Equals(Type, "ethernet", StringComparison.OrdinalIgnoreCase);
    public bool IsWifi => string.Equals(Type, "wifi", StringComparison.OrdinalIgnoreCase);
    public bool IsConnected => State.StartsWith("connected", StringComparison.OrdinalIgnoreCase);

    public string Summary
    {
        get
        {
            var connection = string.IsNullOrWhiteSpace(Connection) || string.Equals(Connection, "--", StringComparison.Ordinal)
                ? "no connection"
                : Connection;
            return $"{Name}  {Type}  {State}  {connection}";
        }
    }
}

internal sealed record WifiAccessPoint(string Ssid, string Bssid, int Signal, string Security)
{
    public bool IsHidden => string.IsNullOrWhiteSpace(Ssid);

    public bool RequiresPassword
    {
        get
        {
            var security = Security.Trim();
            return security.Length > 0 &&
                   !string.Equals(security, "--", StringComparison.OrdinalIgnoreCase) &&
                   !string.Equals(security, "none", StringComparison.OrdinalIgnoreCase) &&
                   !string.Equals(security, "open", StringComparison.OrdinalIgnoreCase);
        }
    }

    public string DisplayName => IsHidden ? "<hidden SSID>" : Ssid;

    public string Summary
        => $"{DisplayName}  {Signal}%  {SecurityDisplay}" + (string.IsNullOrWhiteSpace(Bssid) ? "" : $"  {Bssid}");

    private string SecurityDisplay => string.IsNullOrWhiteSpace(Security) || Security == "--" ? "open" : Security;
}

internal sealed record InstallerIpConfiguration(bool UseDhcp, string? AddressWithPrefix, string? Gateway, IReadOnlyList<string> Dns)
{
    public static InstallerIpConfiguration Dhcp { get; } = new(true, null, null, []);

    public static bool TryCreateStatic(
        string addressWithPrefix,
        string gateway,
        string dnsServers,
        out InstallerIpConfiguration? configuration,
        out string error)
    {
        configuration = null;
        addressWithPrefix = addressWithPrefix.Trim();
        gateway = gateway.Trim();

        if (string.IsNullOrWhiteSpace(addressWithPrefix))
        {
            error = "Static IPv4 address is required.";
            return false;
        }

        var slash = addressWithPrefix.IndexOf('/');
        if (slash <= 0 || slash == addressWithPrefix.Length - 1)
        {
            error = "Static IPv4 address must use CIDR notation, for example 192.168.1.20/24.";
            return false;
        }

        var address = addressWithPrefix[..slash];
        var prefixText = addressWithPrefix[(slash + 1)..];
        if (!IsIpv4(address))
        {
            error = "Static IPv4 address is not valid.";
            return false;
        }

        if (!int.TryParse(prefixText, NumberStyles.None, CultureInfo.InvariantCulture, out var prefix) ||
            prefix < 1 ||
            prefix > 32)
        {
            error = "Static IPv4 prefix must be between 1 and 32.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(gateway))
        {
            error = "Static IPv4 gateway is required.";
            return false;
        }

        if (!IsIpv4(gateway))
        {
            error = "Static IPv4 gateway is not valid.";
            return false;
        }

        var dns = SplitDnsServers(dnsServers);
        foreach (var server in dns)
        {
            if (!IsIpv4(server))
            {
                error = "DNS server is not a valid IPv4 address: " + server;
                return false;
            }
        }

        configuration = new InstallerIpConfiguration(false, addressWithPrefix, gateway, dns);
        error = string.Empty;
        return true;
    }

    public string Summary
        => UseDhcp
            ? "IPv4 DHCP, IPv6 auto"
            : $"IPv4 static {AddressWithPrefix}, gateway {Gateway}, DNS {(Dns.Count == 0 ? "auto" : string.Join(", ", Dns))}, IPv6 auto";

    private static IReadOnlyList<string> SplitDnsServers(string value)
        => string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split([',', ' ', '\t', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool IsIpv4(string value)
        => IPAddress.TryParse(value, out var address) && address.GetAddressBytes().Length == 4;
}

internal sealed record NetworkReadiness(bool HasConnectedDevice, bool HasDefaultRoute)
{
    public bool Ready => HasConnectedDevice && HasDefaultRoute;

    public string Summary
        => Ready
            ? "Network appears ready for downloads."
            : $"Network is not ready: connected device {(HasConnectedDevice ? "yes" : "no")}, default route {(HasDefaultRoute ? "yes" : "no")}.";
}

internal sealed class NetworkManagerClient(HomeHarbor.Tooling.ICommandRunner? runner = null)
{
    private const string WiredProfilePrefix = "HomeHarbor installer wired ";
    private const string WifiProfilePrefix = "HomeHarbor installer wifi ";
    private readonly HomeHarbor.Tooling.ICommandRunner runner = runner ?? new HomeHarbor.Tooling.ProcessCommandRunner();

    public async Task<IReadOnlyList<NetworkDevice>> ListDevicesAsync(CancellationToken cancellationToken = default)
    {
        var result = await runner.RunAsync(
            "nmcli",
            ["--terse", "--fields", "DEVICE,TYPE,STATE,CONNECTION", "device", "status"],
            cancellationToken: cancellationToken);
        _ = result.EnsureSuccess("NetworkManager device status failed");
        return ParseDeviceStatus(result.Stdout);
    }

    public async Task<IReadOnlyList<WifiAccessPoint>> ListWifiAccessPointsAsync(string ifname, CancellationToken cancellationToken = default)
    {
        var result = await runner.RunAsync(
            "nmcli",
            ["--terse", "--fields", "SSID,BSSID,SIGNAL,SECURITY", "device", "wifi", "list", "ifname", ifname, "--rescan", "yes"],
            cancellationToken: cancellationToken);
        _ = result.EnsureSuccess("Wi-Fi scan failed");
        return ParseWifiAccessPoints(result.Stdout);
    }

    public async Task<NetworkReadiness> CheckReadinessAsync(CancellationToken cancellationToken = default)
    {
        var deviceResult = await runner.RunAsync(
            "nmcli",
            ["--terse", "--fields", "DEVICE,TYPE,STATE,CONNECTION", "device", "status"],
            cancellationToken: cancellationToken);
        var hasConnectedDevice = deviceResult.ExitCode == 0 && ParseDeviceStatus(deviceResult.Stdout)
            .Any(device => (device.IsEthernet || device.IsWifi) && device.IsConnected);

        var routeResult = await runner.RunAsync("ip", ["route", "show", "default"], cancellationToken: cancellationToken);
        var hasDefaultRoute = routeResult.ExitCode == 0 &&
                              routeResult.Stdout.Split('\n').Any(line => line.TrimStart().StartsWith("default ", StringComparison.Ordinal));

        return new NetworkReadiness(hasConnectedDevice, hasDefaultRoute);
    }

    public async Task ConnectWiredAsync(
        NetworkDevice device,
        InstallerIpConfiguration ipConfiguration,
        CancellationToken cancellationToken = default)
    {
        if (!device.IsEthernet)
        {
            throw new ArgumentException("Device is not an ethernet interface: " + device.Name, nameof(device));
        }

        var profile = WiredProfileName(device.Name);
        await DeleteConnectionIfExistsAsync(profile, cancellationToken);
        await RunNmcliRequiredAsync(
            ["connection", "add", "type", "ethernet", "ifname", device.Name, "con-name", profile],
            "Failed to create wired connection profile.",
            cancellationToken);
        await ConfigureIpAsync(profile, ipConfiguration, cancellationToken);
        await RunNmcliRequiredAsync(
            ["connection", "up", "id", profile, "ifname", device.Name],
            "Failed to activate wired connection.",
            cancellationToken);
    }

    public async Task ConnectWifiAsync(
        NetworkDevice device,
        string ssid,
        bool hidden,
        string? password,
        InstallerIpConfiguration ipConfiguration,
        CancellationToken cancellationToken = default)
    {
        if (!device.IsWifi)
        {
            throw new ArgumentException("Device is not a Wi-Fi interface: " + device.Name, nameof(device));
        }

        ssid = ssid.Trim();
        if (string.IsNullOrWhiteSpace(ssid))
        {
            throw new ArgumentException("Wi-Fi SSID must not be empty.", nameof(ssid));
        }

        var profile = WifiProfileName(ssid);
        await DeleteConnectionIfExistsAsync(profile, cancellationToken);
        await RunNmcliRequiredAsync(
            ["connection", "add", "type", "wifi", "ifname", device.Name, "con-name", profile, "ssid", ssid],
            "Failed to create Wi-Fi connection profile.",
            cancellationToken);

        if (hidden)
        {
            await RunNmcliRequiredAsync(
                ["connection", "modify", "id", profile, "802-11-wireless.hidden", "yes"],
                "Failed to mark Wi-Fi connection as hidden.",
                cancellationToken);
        }

        if (!string.IsNullOrEmpty(password))
        {
            await RunNmcliRequiredAsync(
                ["connection", "modify", "id", profile, "802-11-wireless-security.key-mgmt", "wpa-psk"],
                "Failed to configure Wi-Fi security.",
                cancellationToken);
        }

        await ConfigureIpAsync(profile, ipConfiguration, cancellationToken);
        await ActivateWifiAsync(profile, device.Name, password, cancellationToken);
    }

    public async Task DisconnectDeviceAsync(NetworkDevice device, CancellationToken cancellationToken = default)
    {
        await RunNmcliRequiredAsync(
            ["device", "disconnect", device.Name],
            "Failed to disconnect network device.",
            cancellationToken);
    }

    internal static IReadOnlyList<NetworkDevice> ParseDeviceStatus(string output)
        => output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(ParseDeviceStatusLine)
            .Where(device => !string.IsNullOrWhiteSpace(device.Name))
            .ToArray();

    internal static IReadOnlyList<WifiAccessPoint> ParseWifiAccessPoints(string output)
        => output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(ParseWifiAccessPointLine)
            .Where(ap => !string.IsNullOrWhiteSpace(ap.Ssid) || !string.IsNullOrWhiteSpace(ap.Bssid))
            .ToArray();

    internal static IReadOnlyList<string> SplitTerseFields(string line)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '\\')
            {
                _ = i + 1 < line.Length ? current.Append(line[++i]) : current.Append(c);

                continue;
            }

            if (c == ':')
            {
                fields.Add(current.ToString());
                _ = current.Clear();
                continue;
            }

            _ = current.Append(c);
        }

        fields.Add(current.ToString());
        return fields;
    }

    internal static string WiredProfileName(string ifname)
        => WiredProfilePrefix + ifname;

    internal static string WifiProfileName(string ssid)
        => WifiProfilePrefix + ssid;

    private async Task ConfigureIpAsync(
        string profile,
        InstallerIpConfiguration ipConfiguration,
        CancellationToken cancellationToken)
    {
        if (ipConfiguration.UseDhcp)
        {
            await RunNmcliRequiredAsync(
                ["connection", "modify", "id", profile, "ipv4.method", "auto", "ipv6.method", "auto"],
                "Failed to configure DHCP.",
                cancellationToken);
            return;
        }

        await RunNmcliRequiredAsync(
            [
                "connection",
                "modify",
                "id",
                profile,
                "ipv4.method",
                "manual",
                "ipv4.addresses",
                ipConfiguration.AddressWithPrefix ?? "",
                "ipv4.gateway",
                ipConfiguration.Gateway ?? "",
                "ipv4.dns",
                string.Join(",", ipConfiguration.Dns),
                "ipv6.method",
                "auto"
            ],
            "Failed to configure static IPv4.",
            cancellationToken);
    }

    private async Task ActivateWifiAsync(
        string profile,
        string ifname,
        string? password,
        CancellationToken cancellationToken)
    {
        string? secretDirectory = null;
        try
        {
            var args = new List<string> { "connection", "up", "id", profile, "ifname", ifname };
            if (!string.IsNullOrEmpty(password))
            {
                var passwordFile = CreateWifiPasswordFile(password, out secretDirectory);
                args.Add("passwd-file");
                args.Add(passwordFile);
            }

            await RunNmcliRequiredAsync(args, "Failed to activate Wi-Fi connection.", cancellationToken);
        }
        finally
        {
            if (secretDirectory is not null && Directory.Exists(secretDirectory))
            {
                Directory.Delete(secretDirectory, recursive: true);
            }
        }
    }

    private static string CreateWifiPasswordFile(string password, out string secretDirectory)
    {
        if (password.Contains('\n') || password.Contains('\r'))
        {
            throw new ArgumentException("Wi-Fi password must not contain line breaks.", nameof(password));
        }

        secretDirectory = Path.Combine(Path.GetTempPath(), "homeharbor-installer-wifi-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(secretDirectory);
        SetOwnerOnlyPermissions(secretDirectory, isDirectory: true);
        var path = Path.Combine(secretDirectory, "wifi-secrets");
        File.WriteAllText(path, "802-11-wireless-security.psk:" + password + Environment.NewLine);
        SetOwnerOnlyPermissions(path, isDirectory: false);
        return path;
    }

    private async Task DeleteConnectionIfExistsAsync(string profile, CancellationToken cancellationToken)
    {
        _ = await runner.RunAsync(
            "nmcli",
            ["connection", "delete", "id", profile],
            cancellationToken: cancellationToken);
    }

    private async Task RunNmcliRequiredAsync(
        IReadOnlyList<string> args,
        string message,
        CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync("nmcli", args, cancellationToken: cancellationToken);
        _ = result.EnsureSuccess(message);
    }

    private static NetworkDevice ParseDeviceStatusLine(string line)
    {
        var fields = SplitTerseFields(line);
        return new NetworkDevice(
            Field(fields, 0),
            Field(fields, 1),
            Field(fields, 2),
            Field(fields, 3));
    }

    private static WifiAccessPoint ParseWifiAccessPointLine(string line)
    {
        var fields = SplitTerseFields(line);
        var signal = int.TryParse(Field(fields, 2), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedSignal)
            ? parsedSignal
            : 0;
        return new WifiAccessPoint(
            Field(fields, 0),
            Field(fields, 1),
            Math.Clamp(signal, 0, 100),
            Field(fields, 3));
    }

    private static string Field(IReadOnlyList<string> fields, int index)
        => index < fields.Count ? fields[index] : string.Empty;

    private static void SetOwnerOnlyPermissions(string path, bool isDirectory)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path,
                isDirectory
                    ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                    : UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}

internal static class InstallerDiagnostics
{
    public static string FormatDiskChoice(DiskInfo disk)
    {
        var details = new List<string>
        {
            disk.Path,
            TerminalGuiInstallerUi.FormatBytesForDisplay(disk.SizeBytes),
            disk.Model
        };

        if (!string.IsNullOrWhiteSpace(disk.Transport))
        {
            details.Add(disk.Transport);
        }

        if (!string.IsNullOrWhiteSpace(disk.Serial))
        {
            details.Add("serial " + disk.Serial);
        }

        if (disk.HasMounts)
        {
            details.Add("mounted: " + string.Join(", ", disk.Mountpoints));
        }

        return string.Join("  ", details);
    }

    public static string BuildSystemSummary(
        InstallerOptions options,
        IReadOnlyList<DiskInfo> disks,
        HostSecurityState security,
        string networkSummary)
        => string.Join(Environment.NewLine, [
            "HomeHarbor live installer status",
            "",
            $"Installer mode:   {options.Mode}",
            $"Payload dir:      {options.PayloadDirectory}",
            $"System OTA arg:   {EmptyAsNone(options.SystemOta)}",
            $"External payloads:{Environment.NewLine}{FormatIndentedList(options.ExternalPayloadDirectories)}",
            "Disk installer:   built-in C# install-disk",
            $"Release key:      {options.PublicKey}",
            $"Stable channel:   {options.StableChannelUrl}",
            $"Daily channel:    {options.DailyChannelUrl}",
            $"Console UI:       {(TerminalGuiInstallerUi.CanUse ? "TUI" : "TUI unavailable")}",
            $"Disk count:       {disks.Count}",
            $"TPM2 device:      {(security.Tpm2Available ? "available" : "not detected")}",
            $"Secure Boot:      {(security.SecureBootEnabled ? "enabled" : "disabled or not detected")}",
            "",
            "Network",
            string.IsNullOrWhiteSpace(networkSummary) ? "No network status was captured." : networkSummary.Trim()
        ]);

    public static string FormatInstallerEnvironment(IEnumerable<KeyValuePair<string, string?>> variables)
    {
        var rendered = variables
            .Where(v => v.Key.StartsWith("HOMEHARBOR_", StringComparison.Ordinal))
            .OrderBy(v => v.Key, StringComparer.Ordinal)
            .Select(v => IsSecretEnvironmentName(v.Key)
                ? v.Key + "=<redacted>"
                : v.Key + "=" + (v.Value ?? string.Empty))
            .ToArray();

        return rendered.Length == 0
            ? "No HOMEHARBOR_* environment variables are set."
            : string.Join(Environment.NewLine, rendered);
    }

    internal static bool IsSecretEnvironmentName(string name)
    {
        var upper = name.ToUpperInvariant();
        return upper.Contains("PASSPHRASE", StringComparison.Ordinal) ||
               upper.Contains("PASSWORD", StringComparison.Ordinal) ||
               upper.Contains("SECRET", StringComparison.Ordinal) ||
               upper.Contains("TOKEN", StringComparison.Ordinal) ||
               upper.Contains("CREDENTIAL", StringComparison.Ordinal) ||
               upper.Contains("PRIVATE_KEY", StringComparison.Ordinal);
    }

    private static string EmptyAsNone(string? value)
        => string.IsNullOrWhiteSpace(value) ? "none" : value;

    private static string FormatIndentedList(IReadOnlyList<string> values)
        => values.Count == 0
            ? "  none"
            : string.Join(Environment.NewLine, values.Select(value => "  - " + value));
}

internal enum InstallerDiagnosticAction
{
    DiskInventory,
    NetworkStatus,
    PayloadSearch,
    TpmAndSecureBoot,
    InstallerEnvironment,
    Back
}

internal sealed record TuiOption<T>(T Value, string Label, string Description);

internal sealed class InstallerCancelledException : Exception;

internal static class InstallerTuiTheme
{
    public const string Base = "HomeHarbor.Base";
    public const string Dialog = "HomeHarbor.Dialog";
    public const string Button = "HomeHarbor.Button";
    public const string List = "HomeHarbor.List";
    public const string Input = "HomeHarbor.Input";
    public const string Report = "HomeHarbor.Report";
    public const string Status = "HomeHarbor.Status";
    public const string Log = "HomeHarbor.Log";
    public const string Warning = "HomeHarbor.Warning";
    public const string Error = "HomeHarbor.Error";
    public const string Destructive = "HomeHarbor.Destructive";

    public static void Apply()
    {
        var baseScheme = Scheme(
            normal: Attr(ColorName16.BrightCyan, ColorName16.Black),
            focus: Attr(ColorName16.Black, ColorName16.BrightCyan),
            active: Attr(ColorName16.Black, ColorName16.BrightYellow),
            highlight: Attr(ColorName16.White, ColorName16.Blue),
            editable: Attr(ColorName16.White, ColorName16.Blue),
            readOnly: Attr(ColorName16.Gray, ColorName16.Black),
            disabled: Attr(ColorName16.DarkGray, ColorName16.Black),
            hotNormal: Attr(ColorName16.BrightYellow, ColorName16.Black),
            hotFocus: Attr(ColorName16.Black, ColorName16.BrightYellow));
        var dialogScheme = Scheme(
            normal: Attr(ColorName16.BrightCyan, ColorName16.Black),
            focus: Attr(ColorName16.Black, ColorName16.BrightCyan),
            active: Attr(ColorName16.Black, ColorName16.BrightYellow),
            highlight: Attr(ColorName16.White, ColorName16.Blue),
            editable: Attr(ColorName16.White, ColorName16.Blue),
            readOnly: Attr(ColorName16.Gray, ColorName16.Black),
            disabled: Attr(ColorName16.DarkGray, ColorName16.Black),
            hotNormal: Attr(ColorName16.BrightYellow, ColorName16.Black),
            hotFocus: Attr(ColorName16.Black, ColorName16.BrightYellow));
        var errorScheme = Scheme(
            normal: Attr(ColorName16.BrightRed, ColorName16.Black),
            focus: Attr(ColorName16.White, ColorName16.Red),
            active: Attr(ColorName16.White, ColorName16.Red),
            highlight: Attr(ColorName16.White, ColorName16.Red),
            editable: Attr(ColorName16.White, ColorName16.Red),
            readOnly: Attr(ColorName16.BrightYellow, ColorName16.Black),
            disabled: Attr(ColorName16.DarkGray, ColorName16.Black),
            hotNormal: Attr(ColorName16.BrightYellow, ColorName16.Black),
            hotFocus: Attr(ColorName16.White, ColorName16.Red));
        var menuScheme = Scheme(
            normal: Attr(ColorName16.Black, ColorName16.BrightCyan),
            focus: Attr(ColorName16.Black, ColorName16.BrightYellow),
            active: Attr(ColorName16.Black, ColorName16.BrightYellow),
            highlight: Attr(ColorName16.White, ColorName16.Blue),
            editable: Attr(ColorName16.Black, ColorName16.BrightCyan),
            readOnly: Attr(ColorName16.Blue, ColorName16.BrightCyan),
            disabled: Attr(ColorName16.DarkGray, ColorName16.Black),
            hotNormal: Attr(ColorName16.Blue, ColorName16.BrightCyan),
            hotFocus: Attr(ColorName16.Red, ColorName16.BrightYellow));

        Add("Base", baseScheme);
        Add("Dialog", dialogScheme);
        Add("Error", errorScheme);
        Add("Menu", menuScheme);
        Add("Accent", Scheme(
            normal: Attr(ColorName16.BrightGreen, ColorName16.Black),
            focus: Attr(ColorName16.Black, ColorName16.BrightGreen),
            active: Attr(ColorName16.Black, ColorName16.BrightYellow),
            highlight: Attr(ColorName16.White, ColorName16.Blue),
            editable: Attr(ColorName16.White, ColorName16.Blue),
            readOnly: Attr(ColorName16.Gray, ColorName16.Black),
            disabled: Attr(ColorName16.DarkGray, ColorName16.Black),
            hotNormal: Attr(ColorName16.BrightYellow, ColorName16.Black),
            hotFocus: Attr(ColorName16.Black, ColorName16.BrightYellow)));

        Add(Base, baseScheme);
        Add(Dialog, dialogScheme);
        Add(Button, Scheme(
            normal: Attr(ColorName16.BrightCyan, ColorName16.Black),
            focus: Attr(ColorName16.Black, ColorName16.BrightCyan),
            active: Attr(ColorName16.Black, ColorName16.BrightYellow),
            highlight: Attr(ColorName16.White, ColorName16.Blue),
            editable: Attr(ColorName16.Black, ColorName16.BrightCyan),
            readOnly: Attr(ColorName16.Gray, ColorName16.Black),
            disabled: Attr(ColorName16.DarkGray, ColorName16.Black),
            hotNormal: Attr(ColorName16.BrightYellow, ColorName16.Black),
            hotFocus: Attr(ColorName16.Black, ColorName16.BrightYellow)));
        Add(List, Scheme(
            normal: Attr(ColorName16.Gray, ColorName16.Black),
            focus: Attr(ColorName16.Black, ColorName16.BrightCyan),
            active: Attr(ColorName16.Black, ColorName16.BrightYellow),
            highlight: Attr(ColorName16.White, ColorName16.Blue),
            editable: Attr(ColorName16.Gray, ColorName16.Black),
            readOnly: Attr(ColorName16.Gray, ColorName16.Black),
            disabled: Attr(ColorName16.DarkGray, ColorName16.Black),
            hotNormal: Attr(ColorName16.BrightCyan, ColorName16.Black),
            hotFocus: Attr(ColorName16.Black, ColorName16.BrightYellow)));
        Add(Input, Scheme(
            normal: Attr(ColorName16.White, ColorName16.Blue),
            focus: Attr(ColorName16.Black, ColorName16.BrightCyan),
            active: Attr(ColorName16.Black, ColorName16.BrightYellow),
            highlight: Attr(ColorName16.White, ColorName16.Blue),
            editable: Attr(ColorName16.White, ColorName16.Blue),
            readOnly: Attr(ColorName16.Gray, ColorName16.Black),
            disabled: Attr(ColorName16.DarkGray, ColorName16.Black),
            hotNormal: Attr(ColorName16.BrightYellow, ColorName16.Blue),
            hotFocus: Attr(ColorName16.Black, ColorName16.BrightYellow)));
        Add(Report, Scheme(
            normal: Attr(ColorName16.Gray, ColorName16.Black),
            focus: Attr(ColorName16.White, ColorName16.Blue),
            active: Attr(ColorName16.Black, ColorName16.BrightYellow),
            highlight: Attr(ColorName16.White, ColorName16.Blue),
            editable: Attr(ColorName16.Gray, ColorName16.Black),
            readOnly: Attr(ColorName16.Gray, ColorName16.Black),
            disabled: Attr(ColorName16.DarkGray, ColorName16.Black),
            hotNormal: Attr(ColorName16.BrightCyan, ColorName16.Black),
            hotFocus: Attr(ColorName16.Black, ColorName16.BrightYellow)));
        Add(Status, Scheme(
            normal: Attr(ColorName16.BrightYellow, ColorName16.Black),
            focus: Attr(ColorName16.Black, ColorName16.BrightYellow),
            active: Attr(ColorName16.Black, ColorName16.BrightYellow),
            highlight: Attr(ColorName16.White, ColorName16.Blue),
            editable: Attr(ColorName16.BrightYellow, ColorName16.Black),
            readOnly: Attr(ColorName16.BrightYellow, ColorName16.Black),
            disabled: Attr(ColorName16.DarkGray, ColorName16.Black),
            hotNormal: Attr(ColorName16.White, ColorName16.Black),
            hotFocus: Attr(ColorName16.Black, ColorName16.BrightYellow)));
        Add(Log, Scheme(
            normal: Attr(ColorName16.BrightGreen, ColorName16.Black),
            focus: Attr(ColorName16.Black, ColorName16.BrightGreen),
            active: Attr(ColorName16.Black, ColorName16.BrightYellow),
            highlight: Attr(ColorName16.White, ColorName16.Blue),
            editable: Attr(ColorName16.BrightGreen, ColorName16.Black),
            readOnly: Attr(ColorName16.BrightGreen, ColorName16.Black),
            disabled: Attr(ColorName16.DarkGray, ColorName16.Black),
            hotNormal: Attr(ColorName16.BrightYellow, ColorName16.Black),
            hotFocus: Attr(ColorName16.Black, ColorName16.BrightYellow)));
        Add(Warning, Scheme(
            normal: Attr(ColorName16.BrightYellow, ColorName16.Black),
            focus: Attr(ColorName16.Black, ColorName16.BrightYellow),
            active: Attr(ColorName16.Black, ColorName16.BrightYellow),
            highlight: Attr(ColorName16.White, ColorName16.Blue),
            editable: Attr(ColorName16.White, ColorName16.Blue),
            readOnly: Attr(ColorName16.BrightYellow, ColorName16.Black),
            disabled: Attr(ColorName16.DarkGray, ColorName16.Black),
            hotNormal: Attr(ColorName16.White, ColorName16.Black),
            hotFocus: Attr(ColorName16.Black, ColorName16.BrightYellow)));
        Add(Error, errorScheme);
        Add(Destructive, Scheme(
            normal: Attr(ColorName16.BrightRed, ColorName16.Black),
            focus: Attr(ColorName16.White, ColorName16.Red),
            active: Attr(ColorName16.White, ColorName16.Red),
            highlight: Attr(ColorName16.White, ColorName16.Red),
            editable: Attr(ColorName16.White, ColorName16.Red),
            readOnly: Attr(ColorName16.BrightRed, ColorName16.Black),
            disabled: Attr(ColorName16.DarkGray, ColorName16.Black),
            hotNormal: Attr(ColorName16.BrightYellow, ColorName16.Black),
            hotFocus: Attr(ColorName16.White, ColorName16.Red)));

    }

    private static void Add(string name, Scheme scheme)
        => SchemeManager.AddScheme(name, scheme);

    private static Scheme Scheme(
        TuiAttribute normal,
        TuiAttribute focus,
        TuiAttribute active,
        TuiAttribute highlight,
        TuiAttribute editable,
        TuiAttribute readOnly,
        TuiAttribute disabled,
        TuiAttribute hotNormal,
        TuiAttribute hotFocus)
        => new()
        {
            Normal = normal,
            Focus = focus,
            Active = active,
            Highlight = highlight,
            Editable = editable,
            ReadOnly = readOnly,
            Disabled = disabled,
            HotNormal = hotNormal,
            HotFocus = hotFocus,
            HotActive = hotFocus
        };

    private static TuiAttribute Attr(ColorName16 foreground, ColorName16 background)
        => new(foreground, background);
}

#pragma warning disable CS0618
internal sealed class TerminalGuiInstallerUi(InstallerOptions options)
{
    internal const string StartupLogPath = "/tmp/homeharbor-installer-tui.log";
    private const string ColorModeKernel16 = "kernel-16";

    private readonly InstallerOptions options = options;
    private readonly NetworkManagerClient networkClient = new();
    private string rescueDirectory = Directory.GetCurrentDirectory();

    public static bool CanUse => !Console.IsInputRedirected && !Console.IsOutputRedirected;

    public async Task<int> RunAsync()
    {
        using IApplication app = Application.Create();
        var driverName = ResolveDriverName();
        ApplySizeDetectionMode();
        WriteStartupLog(driverName);
        _ = app.Init(driverName);
        ApplyScreenSizeOverride(app);

        var forceKernel16Colors = ShouldForceKernel16Colors();
        if (forceKernel16Colors)
        {
            var driver = app.Driver ?? throw new InvalidOperationException("Terminal.Gui driver was not initialized.");
            driver.Force16Colors = true;
        }
        InstallerTuiTheme.Apply();

        while (true)
        {
            var action = Select(
                app,
                "HomeHarbor Live Installer",
                string.Join(Environment.NewLine, [
                    "Use this live environment to install HomeHarbor or repair a system.",
                    "Review status, networking, diagnostics, payloads, and boot security."
                ]),
                [
                    new TuiOption<InstallerMainAction>(InstallerMainAction.Install, "Install HomeHarbor", "Guided disk install."),
                    new TuiOption<InstallerMainAction>(InstallerMainAction.SystemStatus, "System status", "Mode, payloads, network, disks, TPM, Secure Boot."),
                    new TuiOption<InstallerMainAction>(InstallerMainAction.NetworkSettings, "Network settings", "Configure wired or Wi-Fi."),
                    new TuiOption<InstallerMainAction>(InstallerMainAction.Diagnostics, "Diagnostics", "Read-only installer checks."),
                    new TuiOption<InstallerMainAction>(InstallerMainAction.RescueShell, "Rescue shell", "Run maintenance commands."),
                    new TuiOption<InstallerMainAction>(InstallerMainAction.EnableMouse, "Enable mouse", "Start gpm for copy and paste."),
                    new TuiOption<InstallerMainAction>(InstallerMainAction.Quit, "Quit", "Exit.")
                ]);

            switch (action)
            {
                case InstallerMainAction.Install:
                    return await RunInstallFlowAsync(app);
                case InstallerMainAction.SystemStatus:
                    await ShowSystemStatusAsync(app);
                    break;
                case InstallerMainAction.NetworkSettings:
                    await ShowNetworkSettingsAsync(app);
                    break;
                case InstallerMainAction.Diagnostics:
                    await ShowDiagnosticsAsync(app);
                    break;
                case InstallerMainAction.RescueShell:
                    ShowRescueShell(app);
                    break;
                case InstallerMainAction.EnableMouse:
                    await EnableMouseAsync(app);
                    break;
                case InstallerMainAction.Quit:
                    return 0;
                default:
                    return 2;
            }
        }
    }

    internal static string ResolveDriverName()
        => ResolveDriverName(Environment.GetEnvironmentVariable("HOMEHARBOR_INSTALLER_TUI_DRIVER"));

    internal static string ResolveDriverName(string? driver)
    {
        if (string.IsNullOrWhiteSpace(driver))
        {
            return DriverRegistry.Names.DOTNET;
        }

        driver = driver.Trim();
        if (string.Equals(driver, "ansi", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The ANSI Terminal.Gui driver is not supported by the HomeHarbor installer TUI; use 'dotnet'.");
        }

        if (string.Equals(driver, "dotnet", StringComparison.OrdinalIgnoreCase)) return DriverRegistry.Names.DOTNET;
        if (string.Equals(driver, "windows", StringComparison.OrdinalIgnoreCase)) return DriverRegistry.Names.WINDOWS;
        return DriverRegistry.IsRegistered(driver)
            ? driver
            : throw new ArgumentException("Unknown Terminal.Gui driver: " + driver);
    }

    internal static bool ShouldForceKernel16Colors()
    {
        var colorMode = Environment.GetEnvironmentVariable("HOMEHARBOR_INSTALLER_TUI_COLOR_MODE");
        if (!string.IsNullOrWhiteSpace(colorMode))
        {
            colorMode = colorMode.Trim();
            if (string.Equals(colorMode, ColorModeKernel16, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(colorMode, "16", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(colorMode, "16-color", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if (string.Equals(colorMode, "auto", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(colorMode, "terminal", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            throw new ArgumentException("HOMEHARBOR_INSTALLER_TUI_COLOR_MODE must be 'kernel-16', '16-color', 'terminal', or 'auto'.");
        }

        var value = Environment.GetEnvironmentVariable("HOMEHARBOR_INSTALLER_TUI_FORCE_16_COLORS");
        return value is not null &&
               (string.Equals(value, "1", StringComparison.Ordinal) ||
                string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase));
    }

    internal static void ApplySizeDetectionMode()
        => ApplySizeDetectionMode(Environment.GetEnvironmentVariable("HOMEHARBOR_INSTALLER_TUI_SIZE_DETECTION"));

    internal static void ApplySizeDetectionMode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            Driver.SizeDetection = SizeDetectionMode.Polling;
            return;
        }

        if (string.Equals(value, "polling", StringComparison.OrdinalIgnoreCase))
        {
            Driver.SizeDetection = SizeDetectionMode.Polling;
            return;
        }

        if (string.Equals(value, "ansi", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "ansi-query", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("ANSI terminal size detection is not supported by the HomeHarbor installer TUI; use 'polling'.");
        }

        throw new ArgumentException("HOMEHARBOR_INSTALLER_TUI_SIZE_DETECTION must be 'polling'.");
    }

    internal static void ApplyScreenSizeOverride(IApplication app)
    {
        var screenSize = ResolveScreenSizeOverride();
        if (screenSize is null)
        {
            return;
        }

        var (width, height) = screenSize.Value;
        var driver = app.Driver ?? throw new InvalidOperationException("Terminal.Gui driver was not initialized.");
        driver.SetScreenSize(width, height);
    }

    private static (int Width, int Height)? ResolveScreenSizeOverride()
    {
        var columnsValue = Environment.GetEnvironmentVariable("HOMEHARBOR_INSTALLER_TUI_COLUMNS");
        var rowsValue = Environment.GetEnvironmentVariable("HOMEHARBOR_INSTALLER_TUI_ROWS");
        return !string.IsNullOrWhiteSpace(columnsValue) || !string.IsNullOrWhiteSpace(rowsValue)
            ? ((int Width, int Height)?)(
                ParsePositiveDimension(columnsValue, "HOMEHARBOR_INSTALLER_TUI_COLUMNS"),
                ParsePositiveDimension(rowsValue, "HOMEHARBOR_INSTALLER_TUI_ROWS"))
            : TryGetConsoleScreenSize();
    }

    private static (int Width, int Height)? TryGetConsoleScreenSize()
    {
        try
        {
            var width = Console.WindowWidth;
            var height = Console.WindowHeight;
            return width > 0 && height > 0
                ? (width, height)
                : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static int ParsePositiveDimension(string? value, string name)
    {
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : throw new ArgumentException(name + " must be a positive integer.");
    }

    internal static void WriteStartupFailure(Exception exception)
    {
        try
        {
            File.AppendAllText(
                StartupLogPath,
                DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture) +
                " Terminal.Gui startup failed" +
                Environment.NewLine +
                exception +
                Environment.NewLine);
        }
        catch
        {
        }
    }

    private static void WriteStartupLog(string? driverName)
    {
        try
        {
            File.AppendAllText(
                StartupLogPath,
                string.Join(Environment.NewLine, [
                    DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture) + " Starting Terminal.Gui installer",
                    "tty=" + TryReadTty(),
                    "TERM=" + (Environment.GetEnvironmentVariable("TERM") ?? ""),
                    "driver=" + (driverName ?? "default"),
                    "sizeDetection=" + Driver.SizeDetection,
                    "columns=" + (Environment.GetEnvironmentVariable("HOMEHARBOR_INSTALLER_TUI_COLUMNS") ?? ""),
                    "rows=" + (Environment.GetEnvironmentVariable("HOMEHARBOR_INSTALLER_TUI_ROWS") ?? ""),
                    "consoleColumns=" + TryReadConsoleDimension(() => Console.WindowWidth),
                    "consoleRows=" + TryReadConsoleDimension(() => Console.WindowHeight),
                    "colorMode=" + (Environment.GetEnvironmentVariable("HOMEHARBOR_INSTALLER_TUI_COLOR_MODE") ?? ""),
                    "forceKernel16Colors=" + ShouldForceKernel16Colors().ToString(CultureInfo.InvariantCulture),
                    "inputRedirected=" + Console.IsInputRedirected.ToString(CultureInfo.InvariantCulture),
                    "outputRedirected=" + Console.IsOutputRedirected.ToString(CultureInfo.InvariantCulture),
                    ""
                ]));
        }
        catch
        {
        }
    }

    private static string TryReadConsoleDimension(Func<int> read)
    {
        try
        {
            var value = read();
            return value > 0 ? value.ToString(CultureInfo.InvariantCulture) : "";
        }
        catch
        {
            return "";
        }
    }

    private static string TryReadTty()
    {
        try
        {
            var (ExitCode, Output) = ProcessRunner.CaptureAsync("tty", []).GetAwaiter().GetResult();
            return Output.Trim();
        }
        catch
        {
            return "unknown";
        }
    }

    private async Task<int> RunInstallFlowAsync(IApplication app)
    {
        if (!ShowInformation(app, "Before You Install", BuildBeforeInstallText(options.Mode), allowCancel: true))
        {
            return 2;
        }

        var disks = await RunWithStatusAsync(
            app,
            "Scanning disks",
            "Reading installable block devices with lsblk so you can choose the exact target disk.",
            _ => DiskInfo.ListAsync());
        if (disks.Count == 0)
        {
            _ = MessageBox.ErrorQuery(app, "No Disks", "No installable disks were reported by lsblk.", "OK");
            return 1;
        }

        var disk = Select(
            app,
            "Target Disk",
            string.Join(Environment.NewLine, [
                "Choose the physical disk that will receive HomeHarbor.",
                "Everything on the selected disk will be erased, including partitions and filesystems.",
                "Check the path, size, and model carefully before continuing."
            ]),
            disks.Select(d => new TuiOption<DiskInfo>(
                d,
                InstallerDiagnostics.FormatDiskChoice(d),
                d.HasMounts
                    ? "This disk reports mounted filesystems; preflight will refuse it until they are unmounted."
                    : "Erase this disk and install HomeHarbor here.")).ToArray());

        var fullInstall = string.Equals(options.Mode, "full", StringComparison.OrdinalIgnoreCase);
        var selectedChannel = fullInstall
            ? null
            : Select(
                app,
                "Release Channel",
                string.Join(Environment.NewLine, [
                    "Tiny installers download release metadata and the system OTA before writing the disk.",
                    "Choose Stable for normal installs. Use Daily only when validating the newest build."
                ]),
                [
                    new TuiOption<string>("stable", "Stable", "Normal install channel for stable use."),
                    new TuiOption<string>("daily", "Daily", "Latest validation build; useful for testing before release.")
                ]);

        if (!fullInstall && !await EnsureTinyNetworkReadyAsync(app))
        {
            return 2;
        }

        var assets = fullInstall
            ? await ResolveFullAssetsAsync(app)
            : await RunWithStatusAsync(
                app,
                "Preparing Payload",
                $"Downloading {selectedChannel} channel metadata, fetching the system OTA, and verifying its SHA-256 digest.",
                report => InstallerAssets.DownloadLatestAsync(
                    selectedChannel!,
                    string.Equals(selectedChannel, "daily", StringComparison.OrdinalIgnoreCase) ? options.DailyChannelUrl : options.StableChannelUrl,
                    options.PublicKey,
                    report));
        var channel = assets.Channel;

        var expected = "ERASE " + disk.Path;
        var dryRun = await RunWithStatusAsync(
            app,
            "Preflight Validation",
            "Running the installer dry run with the selected disk, payload, and channel metadata.",
            _ => Installer.RunDryRunAsync(options, disk, assets, expected));
        if (!dryRun.Succeeded)
        {
            _ = ShowInformation(app, "Preflight Failed", dryRun.BuildFailureMessage());
            return 1;
        }

        var logPath = CreateInstallLogPath();
        return !ConfirmInstall(app, new InstallSummary(disk, options.Mode, channel, assets, dryRun, logPath), expected)
            ? 2
            : await RunInstallLogAsync(app, disk, assets, expected, logPath);
    }

    private async Task<InstallerAssets> ResolveFullAssetsAsync(IApplication app)
    {
        if (!string.IsNullOrWhiteSpace(options.SystemOta))
        {
            return await RunWithStatusAsync(
                app,
                "Preparing Payload",
                "Loading the configured external system OTA.",
                _ => Task.FromResult(InstallerAssets.FromSystemOtaFile(options.SystemOta, options.PublicKey)));
        }

        var scan = await RunWithStatusAsync(
            app,
            "Scanning Payloads",
            "Checking the ISO payload directory first, then mounted external media for system OTA packages.",
            report =>
            {
                report("Checking ISO payload directory.");
                _ = InstallerAssets.TryFromPayloadDirectory(options.PayloadDirectory, options.PublicKey, out var embedded, out var embeddedError);
                IReadOnlyList<string> external = [];
                if (embedded is null)
                {
                    report("Scanning external payload locations.");
                    external = InstallerAssets.FindExternalSystemOtas(options.ExternalPayloadDirectories);
                }
                else
                {
                    report("Using ISO payload; external media can still be selected manually.");
                }

                return Task.FromResult(new PayloadScan(embedded, embeddedError, external));
            });

        var choices = new List<TuiOption<PayloadChoice>>();
        if (scan.Embedded is not null)
        {
            choices.Add(new TuiOption<PayloadChoice>(
                new PayloadChoice(PayloadChoiceKind.Embedded, scan.Embedded.SystemOta, scan.Embedded),
                "ISO payload",
                scan.Embedded.SystemOta));
        }

        foreach (var path in scan.ExternalSystemOtas)
        {
            if (scan.Embedded is not null && SamePath(path, scan.Embedded.SystemOta))
            {
                continue;
            }

            choices.Add(new TuiOption<PayloadChoice>(
                new PayloadChoice(PayloadChoiceKind.External, path, null),
                Path.GetFileName(path),
                path));
        }

        choices.Add(new TuiOption<PayloadChoice>(
            new PayloadChoice(PayloadChoiceKind.Manual, null, null),
            "Enter path manually",
            "Type a path to a homeharbor-system-ota-*.tar.gz package on mounted media."));

        var body = scan.Embedded is null && !string.IsNullOrWhiteSpace(scan.EmbeddedError)
            ? string.Join(Environment.NewLine, [
                "No ISO system OTA was found in the configured payload directory.",
                "Choose a package discovered on external media or enter a path manually.",
                "The selected package must match the HomeHarbor system OTA naming convention."
            ])
            : string.Join(Environment.NewLine, [
                "Choose the system OTA package that will be installed.",
                "ISO payloads come from the live media and are selected by default.",
                "Manual entry is available when a package is mounted somewhere else."
            ]);
        var selected = Select(app, "System Payload", body, choices);

        return selected.Kind switch
        {
            PayloadChoiceKind.Embedded => selected.Assets ?? throw new InvalidOperationException("ISO payload selection was empty."),
            PayloadChoiceKind.External => InstallerAssets.FromSystemOtaFile(selected.SystemOta ?? "", options.PublicKey),
            PayloadChoiceKind.Manual => PromptSystemOtaPath(app, selected.SystemOta ?? string.Empty),
            _ => throw new InstallerCancelledException()
        };
    }

    private async Task ShowSystemStatusAsync(IApplication app)
    {
        var body = await RunWithStatusAsync(
            app,
            "Gathering Status",
            "Collecting read-only installer, disk, network, TPM, and Secure Boot status.",
            _ => BuildSystemStatusAsync());
        _ = ShowInformation(app, "System Status", body);
    }

    private async Task<string> BuildSystemStatusAsync()
    {
        var disks = await TryListDisksAsync();
        var network = await BuildNetworkDiagnosticAsync();
        return InstallerDiagnostics.BuildSystemSummary(options, disks, HostSecurityState.Detect(), network);
    }

    private async Task ShowNetworkSettingsAsync(IApplication app)
    {
        while (true)
        {
            InstallerNetworkAction action;
            try
            {
                var body = await RunWithStatusAsync(
                    app,
                    "Reading Network",
                    "Checking NetworkManager devices, default route, and active connections.",
                    _ => BuildNetworkMenuBodyAsync());
                action = Select(
                    app,
                    "Network Settings",
                    body,
                    [
                        new TuiOption<InstallerNetworkAction>(InstallerNetworkAction.Wired, "Wired network", "Configure an ethernet interface with DHCP or static IPv4."),
                        new TuiOption<InstallerNetworkAction>(InstallerNetworkAction.Wireless, "Wi-Fi network", "Scan and connect to a visible or hidden WPA-PSK network."),
                        new TuiOption<InstallerNetworkAction>(InstallerNetworkAction.Disconnect, "Disconnect device", "Disconnect a NetworkManager ethernet or Wi-Fi device."),
                        new TuiOption<InstallerNetworkAction>(InstallerNetworkAction.Refresh, "Refresh", "Read the latest NetworkManager status."),
                        new TuiOption<InstallerNetworkAction>(InstallerNetworkAction.Back, "Back", "Return to the main installer menu.")
                    ]);
            }
            catch (InstallerCancelledException)
            {
                return;
            }

            switch (action)
            {
                case InstallerNetworkAction.Wired:
                    await RunNetworkSettingsActionAsync(app, () => ConfigureWiredNetworkAsync(app));
                    break;
                case InstallerNetworkAction.Wireless:
                    await RunNetworkSettingsActionAsync(app, () => ConfigureWifiNetworkAsync(app));
                    break;
                case InstallerNetworkAction.Disconnect:
                    await RunNetworkSettingsActionAsync(app, () => DisconnectNetworkDeviceAsync(app));
                    break;
                case InstallerNetworkAction.Refresh:
                    break;
                case InstallerNetworkAction.Back:
                    return;
            }
        }
    }

    private static async Task RunNetworkSettingsActionAsync(IApplication app, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (InstallerCancelledException)
        {
        }
        catch (Exception ex)
        {
            _ = MessageBox.ErrorQuery(app, "Network Error", ex.Message, "OK");
        }
    }

    private async Task<string> BuildNetworkMenuBodyAsync()
    {
        try
        {
            var readiness = await networkClient.CheckReadinessAsync();
            var devices = await networkClient.ListDevicesAsync();
            var networkDevices = devices.Where(device => device.IsEthernet || device.IsWifi).ToArray();
            var lines = new List<string>
            {
                readiness.Summary,
                "Settings apply only to this live installer session.",
                "Devices:"
            };

            lines.AddRange(networkDevices.Length == 0
                ? ["- no ethernet or Wi-Fi devices reported by NetworkManager"]
                : networkDevices.Take(3).Select(device => "- " + device.Summary));
            if (networkDevices.Length > 3)
            {
                lines.Add("- " + (networkDevices.Length - 3).ToString(CultureInfo.InvariantCulture) + " more device(s)");
            }

            return string.Join(Environment.NewLine, lines);
        }
        catch (Exception ex)
        {
            return string.Join(Environment.NewLine, [
                "NetworkManager status is unavailable.",
                ex.Message,
                "Use diagnostics or rescue shell if NetworkManager is not running."
            ]);
        }
    }

    private async Task ConfigureWiredNetworkAsync(IApplication app)
    {
        var devices = await RunWithStatusAsync(
            app,
            "Finding Wired Devices",
            "Reading ethernet interfaces from NetworkManager.",
            _ => networkClient.ListDevicesAsync());
        var wired = devices.Where(device => device.IsEthernet).ToArray();
        if (wired.Length == 0)
        {
            _ = MessageBox.ErrorQuery(app, "No Wired Devices", "NetworkManager did not report any ethernet interfaces.", "OK");
            return;
        }

        var device = Select(
            app,
            "Wired Device",
            "Choose the ethernet interface to configure for this live installer session.",
            wired.Select(device => new TuiOption<NetworkDevice>(
                device,
                device.Summary,
                device.IsConnected ? "Currently connected." : "Configure this wired interface.")).ToArray());
        var ipConfiguration = PromptIpConfiguration(app, "Wired IPv4");

        _ = await RunWithStatusAsync(
            app,
            "Connecting Wired",
            "Creating a temporary NetworkManager wired profile and activating it.",
            async _ =>
            {
                await networkClient.ConnectWiredAsync(device, ipConfiguration);
                return 0;
            });
        _ = MessageBox.Query(app, "Wired Connected", $"{device.Name} is configured with {ipConfiguration.Summary}.", "OK");
    }

    private async Task ConfigureWifiNetworkAsync(IApplication app)
    {
        var devices = await RunWithStatusAsync(
            app,
            "Finding Wi-Fi Devices",
            "Reading Wi-Fi interfaces from NetworkManager.",
            _ => networkClient.ListDevicesAsync());
        var wifiDevices = devices.Where(device => device.IsWifi).ToArray();
        if (wifiDevices.Length == 0)
        {
            _ = MessageBox.ErrorQuery(app, "No Wi-Fi Devices", "NetworkManager did not report any Wi-Fi interfaces.", "OK");
            return;
        }

        var device = Select(
            app,
            "Wi-Fi Device",
            "Choose the Wi-Fi interface to scan and configure.",
            wifiDevices.Select(device => new TuiOption<NetworkDevice>(
                device,
                device.Summary,
                device.IsConnected ? "Currently connected." : "Scan from this Wi-Fi interface.")).ToArray());

        var accessPoints = await RunWithStatusAsync(
            app,
            "Scanning Wi-Fi",
            "Scanning visible Wi-Fi networks with NetworkManager.",
            _ => networkClient.ListWifiAccessPointsAsync(device.Name));
        var choices = accessPoints
            .Where(ap => !ap.IsHidden)
            .OrderByDescending(ap => ap.Signal)
            .ThenBy(ap => ap.Ssid, StringComparer.OrdinalIgnoreCase)
            .Select(ap => new TuiOption<WifiNetworkChoice>(
                new WifiNetworkChoice(WifiNetworkChoiceKind.AccessPoint, ap),
                ap.Summary,
                ap.RequiresPassword ? "Secured network; a WPA-PSK password is required." : "Open network; no password is required."))
            .ToList();
        choices.Add(new TuiOption<WifiNetworkChoice>(
            new WifiNetworkChoice(WifiNetworkChoiceKind.Hidden, null),
            "Hidden SSID",
            "Manually enter a network name that did not appear in the scan."));

        var choice = Select(
            app,
            "Wi-Fi Network",
            "Choose a visible Wi-Fi network or enter a hidden SSID manually.",
            choices);

        var hidden = choice.Kind == WifiNetworkChoiceKind.Hidden;
        var ssid = hidden
            ? PromptRequiredText(app, "Hidden SSID", "SSID:", "Enter the exact hidden Wi-Fi network name.")
            : choice.AccessPoint?.Ssid ?? string.Empty;
        var password = hidden
            ? PromptWifiPassword(app, "Wi-Fi Password", "Enter the WPA-PSK password, or leave it empty for an open hidden network.", required: false)
            : choice.AccessPoint is { RequiresPassword: true }
                ? PromptWifiPassword(app, "Wi-Fi Password", "Enter the WPA-PSK password for " + ssid + ".", required: true)
                : null;
        var ipConfiguration = PromptIpConfiguration(app, "Wi-Fi IPv4");

        _ = await RunWithStatusAsync(
            app,
            "Connecting Wi-Fi",
            "Creating a temporary NetworkManager Wi-Fi profile and activating it.",
            async _ =>
            {
                await networkClient.ConnectWifiAsync(device, ssid, hidden, password, ipConfiguration);
                return 0;
            });
        _ = MessageBox.Query(app, "Wi-Fi Connected", $"{ssid} is configured with {ipConfiguration.Summary}.", "OK");
    }

    private async Task DisconnectNetworkDeviceAsync(IApplication app)
    {
        var devices = await RunWithStatusAsync(
            app,
            "Reading Devices",
            "Reading NetworkManager ethernet and Wi-Fi devices.",
            _ => networkClient.ListDevicesAsync());
        var networkDevices = devices.Where(device => device.IsEthernet || device.IsWifi).ToArray();
        if (networkDevices.Length == 0)
        {
            _ = MessageBox.ErrorQuery(app, "No Network Devices", "NetworkManager did not report any ethernet or Wi-Fi devices.", "OK");
            return;
        }

        var device = Select(
            app,
            "Disconnect Device",
            "Choose a NetworkManager device to disconnect.",
            networkDevices.Select(device => new TuiOption<NetworkDevice>(
                device,
                device.Summary,
                device.IsConnected ? "Disconnect this active device." : "Device is not currently connected.")).ToArray());

        if (MessageBox.Query(app, "Disconnect Device", "Disconnect " + device.Name + "?", "Cancel", "Disconnect") != 1)
        {
            return;
        }

        _ = await RunWithStatusAsync(
            app,
            "Disconnecting",
            "Disconnecting the selected NetworkManager device.",
            async _ =>
            {
                await networkClient.DisconnectDeviceAsync(device);
                return 0;
            });
        _ = MessageBox.Query(app, "Device Disconnected", device.Name + " was disconnected.", "OK");
    }

    private async Task<bool> EnsureTinyNetworkReadyAsync(IApplication app)
    {
        while (true)
        {
            var readiness = await RunWithStatusAsync(
                app,
                "Checking Network",
                "Tiny installers need network access before downloading release metadata and payloads.",
                _ => networkClient.CheckReadinessAsync());
            if (readiness.Ready)
            {
                return true;
            }

            var choice = MessageBox.Query(
                app,
                "Network Required",
                BuildTinyNetworkWarning(readiness),
                "Network settings",
                "Retry",
                "Continue",
                "Cancel");
            switch (choice)
            {
                case 0:
                    await ShowNetworkSettingsAsync(app);
                    break;
                case 1:
                    break;
                case 2:
                    return true;
                default:
                    return false;
            }
        }
    }

    private static string BuildTinyNetworkWarning(NetworkReadiness readiness)
        => string.Join(Environment.NewLine, [
            "The tiny installer downloads release metadata and the system OTA before writing the disk.",
            readiness.Summary,
            "",
            "Configure networking, retry the check, continue anyway, or cancel the install."
        ]);

    private async Task ShowDiagnosticsAsync(IApplication app)
    {
        while (true)
        {
            InstallerDiagnosticAction action;
            try
            {
                action = Select(
                    app,
                    "Diagnostics",
                    string.Join(Environment.NewLine, [
                        "Run read-only checks from the live installer.",
                        "Use Rescue shell for custom commands after reviewing these focused reports."
                    ]),
                    [
                        new TuiOption<InstallerDiagnosticAction>(InstallerDiagnosticAction.DiskInventory, "Disk inventory", "Show block devices, serials, transports, and mountpoints."),
                        new TuiOption<InstallerDiagnosticAction>(InstallerDiagnosticAction.NetworkStatus, "Network status", "Show interface, route, and NetworkManager status."),
                        new TuiOption<InstallerDiagnosticAction>(InstallerDiagnosticAction.PayloadSearch, "Payload search", "Scan configured ISO and removable-media payload locations."),
                        new TuiOption<InstallerDiagnosticAction>(InstallerDiagnosticAction.TpmAndSecureBoot, "TPM and Secure Boot", "Inspect TPM and boot-security state."),
                        new TuiOption<InstallerDiagnosticAction>(InstallerDiagnosticAction.InstallerEnvironment, "Installer environment", "Show non-secret HOMEHARBOR_* environment variables."),
                        new TuiOption<InstallerDiagnosticAction>(InstallerDiagnosticAction.Back, "Back", "Return to the main installer menu.")
                    ]);
            }
            catch (InstallerCancelledException)
            {
                return;
            }

            if (action == InstallerDiagnosticAction.Back)
            {
                return;
            }

            var report = await RunWithStatusAsync(
                app,
                "Running Diagnostic",
                "Collecting read-only diagnostic output.",
                _ => BuildDiagnosticReportAsync(action));
            _ = ShowInformation(app, DiagnosticTitle(action), report);
        }
    }

    private Task<string> BuildDiagnosticReportAsync(InstallerDiagnosticAction action)
        => action switch
        {
            InstallerDiagnosticAction.DiskInventory => BuildDiskInventoryDiagnosticAsync(),
            InstallerDiagnosticAction.NetworkStatus => BuildNetworkDiagnosticAsync(),
            InstallerDiagnosticAction.PayloadSearch => Task.FromResult(BuildPayloadDiagnostic()),
            InstallerDiagnosticAction.TpmAndSecureBoot => BuildTpmAndSecureBootDiagnosticAsync(),
            InstallerDiagnosticAction.InstallerEnvironment => Task.FromResult(BuildInstallerEnvironmentDiagnostic()),
            _ => Task.FromResult("No diagnostic selected.")
        };

    private static async Task<string> BuildDiskInventoryDiagnosticAsync()
        => string.Join(Environment.NewLine, [
            await CaptureOptionalCommandAsync("lsblk", ["-o", "NAME,PATH,SIZE,TYPE,MODEL,SERIAL,TRAN,MOUNTPOINTS"]),
            "",
            "Installer disk choices:",
            FormatDiskList(await TryListDisksAsync())
        ]);

    private static async Task<string> BuildNetworkDiagnosticAsync()
        => string.Join(Environment.NewLine, [
            await CaptureOptionalCommandAsync("ip", ["-brief", "address"]),
            await CaptureOptionalCommandAsync("ip", ["route"]),
            await CaptureOptionalCommandAsync("nmcli", ["general", "status"]),
            await CaptureOptionalCommandAsync("nmcli", ["device", "status"]),
            await CaptureOptionalCommandAsync("nmcli", ["radio", "wifi"]),
            await CaptureOptionalCommandAsync("nmcli", ["device", "wifi", "list"])
        ]);

    private string BuildPayloadDiagnostic()
    {
        var lines = new List<string>
        {
            "Configured payload locations",
            "",
            "ISO payload directory:",
            "  " + options.PayloadDirectory,
            "",
            "External payload search roots:"
        };
        lines.AddRange(options.ExternalPayloadDirectories.Count == 0
            ? ["  none"]
            : options.ExternalPayloadDirectories.Select(path => "  - " + path));

        lines.Add("");
        if (InstallerAssets.TryFromPayloadDirectory(options.PayloadDirectory, options.PublicKey, out var embedded, out var embeddedError) && embedded is not null)
        {
            lines.Add("ISO payload:");
            lines.Add("  " + embedded.SystemOta);
            lines.Add("  version: " + embedded.Version);
        }
        else
        {
            lines.Add("ISO payload:");
            lines.Add("  not found" + (string.IsNullOrWhiteSpace(embeddedError) ? "" : ": " + embeddedError));
        }

        var external = InstallerAssets.FindExternalSystemOtas(options.ExternalPayloadDirectories);
        lines.Add("");
        lines.Add("External system OTA candidates:");
        lines.AddRange(external.Count == 0 ? ["  none"] : external.Select(path => "  - " + path));
        return string.Join(Environment.NewLine, lines);
    }

    private static async Task<string> BuildTpmAndSecureBootDiagnosticAsync()
        => string.Join(Environment.NewLine, [
            HostSecurityState.Detect().BuildSummary(),
            "",
            await CaptureOptionalCommandAsync("mokutil", ["--sb-state"])
        ]);

    private static string BuildInstallerEnvironmentDiagnostic()
    {
        var variables = Environment.GetEnvironmentVariables()
            .Keys
            .Cast<string>()
            .Select(key => new KeyValuePair<string, string?>(key, Environment.GetEnvironmentVariable(key)));

        return string.Join(Environment.NewLine, [
            "Installer environment",
            "",
            InstallerDiagnostics.FormatInstallerEnvironment(variables)
        ]);
    }

    private static async Task<IReadOnlyList<DiskInfo>> TryListDisksAsync()
    {
        try
        {
            return await DiskInfo.ListAsync();
        }
        catch
        {
            return [];
        }
    }

    private static string FormatDiskList(IReadOnlyList<DiskInfo> disks)
        => disks.Count == 0
            ? "No installable disks were reported by lsblk."
            : string.Join(Environment.NewLine, disks.Select(disk => "- " + InstallerDiagnostics.FormatDiskChoice(disk)));

    private static string DiagnosticTitle(InstallerDiagnosticAction action)
        => action switch
        {
            InstallerDiagnosticAction.DiskInventory => "Disk Inventory",
            InstallerDiagnosticAction.NetworkStatus => "Network Status",
            InstallerDiagnosticAction.PayloadSearch => "Payload Search",
            InstallerDiagnosticAction.TpmAndSecureBoot => "TPM And Secure Boot",
            InstallerDiagnosticAction.InstallerEnvironment => "Installer Environment",
            _ => "Diagnostics"
        };

    private static async Task<string> CaptureOptionalCommandAsync(string fileName, IReadOnlyList<string> args)
    {
        var command = fileName + (args.Count == 0 ? "" : " " + string.Join(' ', args));
        try
        {
            var (ExitCode, Output) = await ProcessRunner.CaptureAsync(fileName, args);
            var status = ExitCode == 0 ? "" : $" [exit {ExitCode}]";
            var output = string.IsNullOrWhiteSpace(Output) ? "(no output)" : Output.Trim();
            return "$ " + command + status + Environment.NewLine + output;
        }
        catch (Exception ex)
        {
            return "$ " + command + " [unavailable]" + Environment.NewLine + ex.Message;
        }
    }

    private static Dialog ThemedDialog(string title, Dim width, Dim height, string schemeName = InstallerTuiTheme.Dialog)
        => new()
        {
            Title = title,
            Width = width,
            Height = height,
            SchemeName = schemeName
        };

    private static Button ThemedButton(string text, string schemeName = InstallerTuiTheme.Button)
        => new()
        {
            Text = text,
            SchemeName = schemeName
        };

    private static TextField ThemedTextField(string text = "", string schemeName = InstallerTuiTheme.Input)
        => new()
        {
            Text = text,
            SchemeName = schemeName
        };

    private InstallerAssets PromptSystemOtaPath(IApplication app, string initialPath)
    {
        while (true)
        {
            var dialog = ThemedDialog("System OTA Path", Dim.Percent(76), 10);
            _ = dialog.Add(new Label
            {
                X = 1,
                Y = 0,
                Width = Dim.Fill(),
                Text = "Path to homeharbor-system-ota-*.tar.gz:"
            });
            var pathField = ThemedTextField(initialPath);
            pathField.X = 1;
            pathField.Y = 3;
            pathField.Width = Dim.Fill();
            _ = dialog.Add(pathField);
            dialog.AddButton(ThemedButton("_Cancel"));
            dialog.AddButton(ThemedButton("_Use"));
            _ = pathField.SetFocus();
            _ = app.Run(dialog);

            if (dialog.Result != 1)
            {
                throw new InstallerCancelledException();
            }

            var path = pathField.Text?.ToString() ?? string.Empty;
            try
            {
                return InstallerAssets.FromSystemOtaFile(path, options.PublicKey);
            }
            catch (Exception ex)
            {
                _ = MessageBox.ErrorQuery(app, "Invalid System OTA", ex.Message, "OK");
                initialPath = path;
            }
        }
    }

    private static bool SamePath(string left, string right)
    {
        try
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return string.Equals(left, right, StringComparison.Ordinal);
        }
    }

    private static bool ShowInformation(IApplication app, string heading, string body, bool allowCancel = false)
    {
        var dialog = ThemedDialog(heading, Dim.Percent(78), Dim.Percent(68));
        _ = dialog.Add(new TextView
        {
            X = 1,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(1),
            ReadOnly = true,
            SchemeName = InstallerTuiTheme.Report,
            Text = body
        });

        if (allowCancel)
        {
            dialog.AddButton(ThemedButton("_Cancel"));
        }

        dialog.AddButton(ThemedButton("C_ontinue"));
        _ = app.Run(dialog);
        return !allowCancel || dialog.Result == 1;
    }

    private static string BuildBeforeInstallText(string mode)
    {
        var fullInstall = string.Equals(mode, "full", StringComparison.OrdinalIgnoreCase);
        var payloadText = fullInstall
            ? "This full ISO uses system OTA payloads from the live media first, then mounted external media if needed."
            : "This tiny ISO downloads release metadata and the system OTA over the network before writing the disk.";

        return string.Join(Environment.NewLine, [
            "HomeHarbor will be installed to one target disk.",
            "",
            "What to expect:",
            "- The target disk will be completely erased.",
            "- " + payloadText,
            "- The final screen requires an exact ERASE confirmation before anything is written.",
            "",
            "Keep the machine connected to reliable power. For tiny installs, make sure the network is available."
        ]);
    }

    private static T Select<T>(IApplication app, string heading, string body, IReadOnlyList<TuiOption<T>> choices)
    {
        if (choices.Count == 0)
        {
            throw new ArgumentException("At least one option is required.", nameof(choices));
        }

        var bodyHeight = SelectBodyHeight(body);
        var dialog = ThemedDialog(heading, Dim.Percent(76), Dim.Percent(72));
        _ = dialog.Add(new Label
        {
            X = 1,
            Y = 0,
            Width = Dim.Fill(),
            Height = bodyHeight,
            Text = body
        });

        var rendered = choices.Select(c => $"{c.Label} - {c.Description}").ToList();
        var list = CreateSelectList(rendered);
        list.Y = bodyHeight + 1;
        var listAccepted = false;
        list.Accepted += (_, _) =>
        {
            if (list.SelectedItem is null)
            {
                return;
            }

            listAccepted = true;
            app.RequestStop(dialog);
        };
        _ = dialog.Add(list);
        dialog.AddButton(ThemedButton("_Cancel"));
        dialog.AddButton(ThemedButton("_Select"));
        _ = list.SetFocus();
        _ = app.Run(dialog);

        return (!listAccepted && dialog.Result != 1) || list.SelectedItem is null
            ? throw new InstallerCancelledException()
            : choices[list.SelectedItem.Value].Value;
    }

    internal static ListView CreateSelectList(IReadOnlyList<string> rendered)
    {
        var list = new ListView
        {
            X = 1,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            SchemeName = InstallerTuiTheme.List,
            Source = new ListWrapper<string>(new ObservableCollection<string>(rendered))
        };
        if (rendered.Count > 0)
        {
            list.SelectedItem = 0;
        }

        return list;
    }

    private static int SelectBodyHeight(string body)
        => Math.Clamp(body.Split('\n').Length, 2, 6);

    private static InstallerIpConfiguration PromptIpConfiguration(IApplication app, string heading)
    {
        var mode = Select(
            app,
            heading,
            "Choose IPv4 addressing for this temporary NetworkManager connection. IPv6 remains automatic.",
            [
                new TuiOption<InstallerIpMode>(InstallerIpMode.Dhcp, "DHCP", "Automatically request address, gateway, and DNS."),
                new TuiOption<InstallerIpMode>(InstallerIpMode.Static, "Static IPv4", "Enter address/prefix, gateway, and optional DNS servers.")
            ]);
        if (mode == InstallerIpMode.Dhcp)
        {
            return InstallerIpConfiguration.Dhcp;
        }

        while (true)
        {
            var dialog = ThemedDialog(heading, Dim.Percent(72), 16);
            _ = dialog.Add(new Label
            {
                X = 1,
                Y = 0,
                Width = Dim.Fill(),
                Text = "Static IPv4 settings for this live installer session."
            });
            _ = dialog.Add(new Label { X = 1, Y = 3, Width = 18, Text = "Address/prefix:" });
            var addressField = ThemedTextField();
            addressField.X = 20;
            addressField.Y = 3;
            addressField.Width = Dim.Fill();
            _ = dialog.Add(addressField);

            _ = dialog.Add(new Label { X = 1, Y = 5, Width = 18, Text = "Gateway:" });
            var gatewayField = ThemedTextField();
            gatewayField.X = 20;
            gatewayField.Y = 5;
            gatewayField.Width = Dim.Fill();
            _ = dialog.Add(gatewayField);

            _ = dialog.Add(new Label { X = 1, Y = 7, Width = 18, Text = "DNS:" });
            var dnsField = ThemedTextField();
            dnsField.X = 20;
            dnsField.Y = 7;
            dnsField.Width = Dim.Fill();
            _ = dialog.Add(dnsField);

            _ = dialog.Add(new Label
            {
                X = 1,
                Y = 10,
                Width = Dim.Fill(),
                SchemeName = InstallerTuiTheme.Status,
                Text = "Example address: 192.168.1.20/24. DNS is optional; separate servers with commas or spaces."
            });
            dialog.AddButton(ThemedButton("_Cancel"));
            dialog.AddButton(ThemedButton("_Use"));
            _ = addressField.SetFocus();
            _ = app.Run(dialog);

            if (dialog.Result != 1)
            {
                throw new InstallerCancelledException();
            }

            if (InstallerIpConfiguration.TryCreateStatic(
                    addressField.Text?.ToString() ?? string.Empty,
                    gatewayField.Text?.ToString() ?? string.Empty,
                    dnsField.Text?.ToString() ?? string.Empty,
                    out var configuration,
                    out var error) &&
                configuration is not null)
            {
                return configuration;
            }

            _ = MessageBox.ErrorQuery(app, "Invalid Static IPv4", error, "OK");
        }
    }

    private static string PromptRequiredText(IApplication app, string heading, string label, string body)
    {
        while (true)
        {
            var dialog = ThemedDialog(heading, Dim.Percent(68), 11);
            _ = dialog.Add(new Label { X = 1, Y = 0, Width = Dim.Fill(), Text = body });
            _ = dialog.Add(new Label { X = 1, Y = 3, Width = 14, Text = label });
            var field = ThemedTextField();
            field.X = 16;
            field.Y = 3;
            field.Width = Dim.Fill();
            _ = dialog.Add(field);
            dialog.AddButton(ThemedButton("_Cancel"));
            dialog.AddButton(ThemedButton("C_ontinue"));
            _ = field.SetFocus();
            _ = app.Run(dialog);

            if (dialog.Result != 1)
            {
                throw new InstallerCancelledException();
            }

            var value = field.Text?.ToString()?.Trim() ?? string.Empty;
            if (value.Length > 0)
            {
                return value;
            }

            _ = MessageBox.ErrorQuery(app, heading + " Required", label.TrimEnd(':') + " must not be empty.", "OK");
        }
    }

    private static string? PromptWifiPassword(IApplication app, string heading, string body, bool required)
    {
        while (true)
        {
            var dialog = ThemedDialog(heading, Dim.Percent(68), 11);
            _ = dialog.Add(new Label { X = 1, Y = 0, Width = Dim.Fill(), Text = body });
            _ = dialog.Add(new Label { X = 1, Y = 3, Width = 12, Text = "Password:" });
            var passwordField = ThemedTextField();
            passwordField.X = 14;
            passwordField.Y = 3;
            passwordField.Width = Dim.Fill();
            passwordField.Secret = true;
            _ = dialog.Add(passwordField);
            dialog.AddButton(ThemedButton("_Cancel"));
            dialog.AddButton(ThemedButton("C_ontinue"));
            _ = passwordField.SetFocus();
            _ = app.Run(dialog);

            if (dialog.Result != 1)
            {
                throw new InstallerCancelledException();
            }

            var password = passwordField.Text?.ToString() ?? string.Empty;
            if (password.Length == 0)
            {
                if (!required)
                {
                    return null;
                }

                _ = MessageBox.ErrorQuery(app, "Password Required", "This Wi-Fi network requires a WPA-PSK password.", "OK");
                continue;
            }

            if (password.Contains('\n') || password.Contains('\r'))
            {
                _ = MessageBox.ErrorQuery(app, "Invalid Password", "Wi-Fi password must not contain line breaks.", "OK");
                continue;
            }

            return password;
        }
    }

    private static bool ConfirmInstall(IApplication app, InstallSummary summary, string expected)
    {
        var dialog = ThemedDialog("Ready To Install", Dim.Percent(82), Dim.Percent(78), InstallerTuiTheme.Warning);
        _ = dialog.Add(new TextView
        {
            X = 1,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(4),
            ReadOnly = true,
            SchemeName = InstallerTuiTheme.Warning,
            Text = BuildReadySummary(summary, expected)
        });
        _ = dialog.Add(new Label
        {
            X = 1,
            Y = Pos.AnchorEnd(3),
            Width = Dim.Fill(),
            SchemeName = InstallerTuiTheme.Destructive,
            Text = BuildInstallConfirmationPrompt(expected)
        });
        var confirmation = ThemedTextField(schemeName: InstallerTuiTheme.Destructive);
        confirmation.X = 1;
        confirmation.Y = Pos.AnchorEnd(2);
        confirmation.Width = Dim.Fill();
        _ = dialog.Add(confirmation);
        dialog.AddButton(ThemedButton("_Cancel"));
        dialog.AddButton(ThemedButton("_Install", InstallerTuiTheme.Destructive));
        confirmation.Accepted += (_, _) =>
        {
            dialog.Result = 1;
            app.RequestStop(dialog);
        };
        _ = confirmation.SetFocus();
        _ = app.Run(dialog);

        return dialog.Result == 1 && string.Equals(confirmation.Text?.ToString(), expected, StringComparison.Ordinal);
    }

    internal static string BuildInstallConfirmationPrompt(string expected)
        => $"Type {expected} to install:";

    internal static string BuildReadySummary(InstallSummary summary, string expected)
    {
        var fullInstall = string.Equals(summary.Mode, "full", StringComparison.OrdinalIgnoreCase);
        var modeLabel = fullInstall ? "full ISO" : "tiny network";
        var channelMetadata = string.IsNullOrWhiteSpace(summary.Assets.ChannelFile)
            ? "none"
            : summary.Assets.ChannelFile;

        return string.Join(Environment.NewLine, [
            "Installation plan",
            "",
            $"Target disk:      {summary.Disk.Path}",
            $"Disk size:        {FormatBytes(summary.Disk.SizeBytes)}",
            $"Disk model:       {summary.Disk.Model}",
            $"Installer mode:   {modeLabel}",
            $"Release channel:  {summary.Channel}",
            $"Version:          {summary.Assets.Version}",
            $"System OTA:       {summary.Assets.SystemOta}",
            $"Release key:      {summary.Assets.PublicKey}",
            $"Channel metadata: {channelMetadata}",
            $"Install log:      {summary.LogPath}",
            "",
            summary.DryRun.FormatForConfirmation(),
            "",
            "Destructive action",
            "Everything on the target disk will be erased, including existing partitions and filesystems.",
            $"Required confirmation: {expected}"
        ]);
    }

    private async Task<int> RunInstallLogAsync(
        IApplication app,
        DiskInfo disk,
        InstallerAssets assets,
        string expected,
        string logPath)
    {
        var dialog = ThemedDialog("Installing HomeHarbor", Dim.Percent(92), Dim.Percent(86), InstallerTuiTheme.Log);
        var intro = BuildInstallLogIntro(disk, assets, expected, logPath);
        File.WriteAllText(logPath, intro);
        var log = new TextView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            ReadOnly = true,
            SchemeName = InstallerTuiTheme.Log,
            Text = intro
        };
        _ = dialog.Add(log);

        var completion = new TaskCompletionSource<int>();
        var finished = false;
        var logLock = new object();
        dialog.Accepting += (_, e) => e.Handled = !finished;
        var started = false;
        _ = app.AddTimeout(TimeSpan.FromMilliseconds(50), () =>
        {
            if (started)
            {
                return false;
            }

            started = true;
            _ = Task.Run(async () =>
            {
                int exitCode;
                try
                {
                    exitCode = await Installer.RunInstallDiskAsync(
                        options,
                        disk,
                        assets,
                        expected,
                        chunk =>
                        {
                            AppendLogFile(logPath, chunk, logLock);
                            app.Invoke(() => AppendLog(log, chunk));
                        });
                }
                catch (Exception ex)
                {
                    exitCode = 1;
                    var message = ex.Message + Environment.NewLine;
                    AppendLogFile(logPath, message, logLock);
                    app.Invoke(() => AppendLog(log, message));
                }

                app.Invoke(() =>
                {
                    var completionMessage = Environment.NewLine + BuildCompletionMessage(exitCode, logPath) + Environment.NewLine;
                    AppendLogFile(logPath, completionMessage, logLock);
                    AppendLog(log, completionMessage);
                    finished = true;
                    completion.SetResult(exitCode);
                    app.RequestStop(dialog);
                });
            });
            return false;
        });

        _ = app.Run(dialog);
        var result = await completion.Task;
        if (result == 0)
        {
            _ = MessageBox.Query(
                app,
                "Install Complete",
                BuildCompletionMessage(result, logPath),
                "OK");
        }
        else
        {
            await ShowInstallFailedActionsAsync(app, result, logPath);
        }

        return result;
    }

    private async Task ShowInstallFailedActionsAsync(IApplication app, int exitCode, string logPath)
    {
        var choice = MessageBox.Query(
            app,
            "Install Failed",
            BuildCompletionMessage(exitCode, logPath),
            "Diagnostics",
            "Rescue shell",
            "OK");

        if (choice == 0)
        {
            await ShowDiagnosticsAsync(app);
        }
        else if (choice == 1)
        {
            ShowRescueShell(app);
        }
    }

    private static string BuildInstallLogIntro(DiskInfo disk, InstallerAssets assets, string expected, string logPath)
        => string.Join(Environment.NewLine, [
            "HomeHarbor installer is starting.",
            "",
            "Disk installer: built-in C# install-disk",
            $"Target disk:    {disk.Path}  {FormatBytes(disk.SizeBytes)}  {disk.Model}",
            $"System OTA:     {assets.SystemOta}",
            $"Release key:    {assets.PublicKey}",
            $"Confirmation:   {expected}",
            $"Log file:       {logPath}",
            "",
            "Streaming installer output below.",
            ""
        ]);

    internal static string BuildCompletionMessage(int exitCode, string? logPath = null)
    {
        if (exitCode != 0)
        {
            return string.Join(Environment.NewLine, [
                "Installation failed with exit code " + exitCode + ".",
                "",
                "Review the installer log above for the failing command or validation message.",
                string.IsNullOrWhiteSpace(logPath) ? "No log file path was recorded." : "Saved log: " + logPath,
                "You can return to the live environment, check disks or payloads, and run the installer again."
            ]);
        }

        var lines = new List<string>
        {
            "HomeHarbor installation completed successfully.",
            "",
            "Next steps:",
            "1. Remove the installer media.",
            "2. Reboot into the newly installed HomeHarbor system."
        };
        if (!string.IsNullOrWhiteSpace(logPath))
        {
            lines.Add("Installer log: " + logPath);
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string CreateInstallLogPath()
        => Path.Combine(
            Path.GetTempPath(),
            "homeharbor-installer-" + DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".log");

    private static void AppendLogFile(string path, string value, object sync)
    {
        lock (sync)
        {
            File.AppendAllText(path, value);
        }
    }

    private static async Task<T> RunWithStatusAsync<T>(
        IApplication app,
        string heading,
        string body,
        Func<Action<string>, Task<T>> work)
    {
        var dialog = ThemedDialog(heading, Dim.Percent(66), 9, InstallerTuiTheme.Status);
        var label = new Label
        {
            X = 1,
            Y = 0,
            Width = Dim.Fill(),
            Height = 4,
            SchemeName = InstallerTuiTheme.Status,
            Text = body + Environment.NewLine + Environment.NewLine + "Please wait..."
        };
        _ = dialog.Add(label);

        var completion = new TaskCompletionSource<T>();
        dialog.Accepting += (_, e) => e.Handled = !completion.Task.IsCompleted;
        var started = false;
        _ = app.AddTimeout(TimeSpan.FromMilliseconds(50), () =>
        {
            if (started)
            {
                return false;
            }

            started = true;
            _ = Task.Run(async () =>
            {
                try
                {
                    var result = await work(message => app.Invoke(() => label.Text = message + Environment.NewLine + Environment.NewLine + "Please wait..."));
                    app.Invoke(() =>
                    {
                        completion.SetResult(result);
                        app.RequestStop(dialog);
                    });
                }
                catch (Exception ex)
                {
                    app.Invoke(() =>
                    {
                        completion.SetException(ex);
                        app.RequestStop(dialog);
                    });
                }
            });
            return false;
        });

        _ = app.Run(dialog);
        return await completion.Task;
    }

    private static async Task EnableMouseAsync(IApplication app)
    {
        var (ExitCode, Output) = await ProcessRunner.CaptureAsync("systemctl", ["start", "gpm.service"]);
        if (ExitCode == 0)
        {
            _ = MessageBox.Query(app, "Mouse Enabled", "gpm.service started.", "OK");
            return;
        }

        _ = MessageBox.ErrorQuery(app, "Mouse Enable Failed", Output.Trim(), "OK");
    }

    private void ShowRescueShell(IApplication app)
    {
        var dialog = ThemedDialog("Rescue Shell", Dim.Percent(94), Dim.Percent(88), InstallerTuiTheme.Log);
        var output = new TextView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(2),
            ReadOnly = true,
            SchemeName = InstallerTuiTheme.Log,
            Text = string.Join(Environment.NewLine, [
                "HomeHarbor rescue shell. Use cd, clear, exit, or any command available in the live environment.",
                "",
                "Useful checks:",
                "  lsblk -o NAME,PATH,SIZE,TYPE,MODEL,SERIAL,TRAN,MOUNTPOINTS",
                "  ip -brief address && ip route",
                "  nmcli general status",
                "  nmcli device status",
                "  nmcli device wifi list",
                "  mokutil --sb-state",
                "  journalctl -b -p warning --no-pager",
                ""
            ])
        };
        var prompt = new Label
        {
            X = 0,
            Y = Pos.AnchorEnd(1),
            Width = 18,
            SchemeName = InstallerTuiTheme.Status,
            Text = ShortPrompt()
        };
        var input = ThemedTextField(schemeName: InstallerTuiTheme.Input);
        input.X = Pos.Right(prompt) + 1;
        input.Y = Pos.AnchorEnd(1);
        input.Width = Dim.Fill();
        input.Accepted += async (_, _) =>
        {
            var command = input.Text?.ToString() ?? string.Empty;
            input.Text = string.Empty;
            await ExecuteRescueCommandAsync(app, output, prompt, input, dialog, command);
        };
        dialog.Add(output, prompt, input);
        dialog.AddButton(ThemedButton("_Close"));
        _ = input.SetFocus();
        _ = app.Run(dialog);
    }

    private async Task ExecuteRescueCommandAsync(
        IApplication app,
        TextView output,
        Label prompt,
        TextField input,
        Dialog dialog,
        string rawCommand)
    {
        var command = rawCommand.Trim();
        if (command.Length == 0)
        {
            return;
        }

        AppendLog(output, $"{ShortPrompt()} {command}{Environment.NewLine}");
        if (string.Equals(command, "exit", StringComparison.Ordinal))
        {
            app.RequestStop(dialog);
            return;
        }

        if (string.Equals(command, "clear", StringComparison.Ordinal))
        {
            output.Text = string.Empty;
            return;
        }

        if (command == "pwd")
        {
            AppendLog(output, rescueDirectory + Environment.NewLine);
            return;
        }

        if (command == "cd" || command.StartsWith("cd ", StringComparison.Ordinal))
        {
            try
            {
                ChangeRescueDirectory(command);
                prompt.Text = ShortPrompt();
            }
            catch (Exception ex)
            {
                AppendLog(output, ex.Message + Environment.NewLine);
            }

            return;
        }

        try
        {
            input.Enabled = false;
            var (ExitCode, Output) = await ProcessRunner.CaptureAsync("/bin/bash", ["-lc", command], rescueDirectory);
            AppendLog(output, Output);
            if (ExitCode != 0)
            {
                AppendLog(output, $"[exit {ExitCode}]{Environment.NewLine}");
            }
        }
        catch (Exception ex)
        {
            AppendLog(output, ex.Message + Environment.NewLine);
        }
        finally
        {
            input.Enabled = true;
            _ = input.SetFocus();
        }
    }

    private void ChangeRescueDirectory(string command)
    {
        var target = command == "cd" ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : command[3..].Trim();
        target = Unquote(target);
        if (target.StartsWith("~/", StringComparison.Ordinal))
        {
            target = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), target[2..]);
        }
        else if (target == "~")
        {
            target = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }
        else if (!Path.IsPathRooted(target))
        {
            target = Path.GetFullPath(Path.Combine(rescueDirectory, target));
        }

        if (!Directory.Exists(target))
        {
            throw new InvalidOperationException("directory not found: " + target);
        }

        rescueDirectory = target;
    }

    private string ShortPrompt()
    {
        var name = Path.GetFileName(rescueDirectory.TrimEnd(Path.DirectorySeparatorChar));
        return string.IsNullOrWhiteSpace(name) ? "# /" : "# " + name;
    }

    private static string Unquote(string value)
        => value.Length >= 2 &&
           ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\''))
            ? value[1..^1]
            : value;

    private static void AppendLog(TextView view, string value)
    {
        view.Text = (view.Text?.ToString() ?? string.Empty) + value;
        _ = view.MoveEnd();
    }

    internal static string FormatBytesForDisplay(long bytes)
        => FormatBytes(bytes);

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.#} {units[unit]}";
    }
}
#pragma warning restore CS0618

internal static class ProcessRunner
{
    public static async Task<int> RunStreamingAsync(string fileName, IReadOnlyList<string> args)
    {
        var start = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false
        };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Failed to start " + fileName);
        await process.WaitForExitAsync();
        return process.ExitCode;
    }

    public static async Task<int> RunStreamingAsync(string fileName, IReadOnlyList<string> args, Action<string> output)
    {
        var start = new ProcessStartInfo(fileName)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Failed to start " + fileName);
        var stdout = ReadPipeAsync(process.StandardOutput, output);
        var stderr = ReadPipeAsync(process.StandardError, output);
        await process.WaitForExitAsync();
        await Task.WhenAll(stdout, stderr);
        return process.ExitCode;
    }

    public static async Task<(int ExitCode, string Output)> CaptureAsync(
        string fileName,
        IReadOnlyList<string> args,
        string? workingDirectory = null)
    {
        var start = new ProcessStartInfo(fileName)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            WorkingDirectory = workingDirectory ?? string.Empty
        };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Failed to start " + fileName);
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        return (process.ExitCode, stdout + stderr);
    }

    private static async Task ReadPipeAsync(TextReader reader, Action<string> output)
    {
        var buffer = new char[4096];
        while (true)
        {
            var read = await reader.ReadAsync(buffer);
            if (read == 0)
            {
                return;
            }

            output(new string(buffer, 0, read));
        }
    }
}
