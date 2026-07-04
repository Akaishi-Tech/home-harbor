using System.Buffers;
using System.Buffers.Binary;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;

[assembly: InternalsVisibleTo("HomeHarbor.Tests")]

try
{
    return await RecoveryProgram.RunAsync(args, CancellationToken.None);
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    return ex is ArgumentException ? 2 : 1;
}

internal static class RecoveryProgram
{
    public static Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
        => RunAsync(
            args,
            static _ => RecoveryConsole.RunAsync(),
            static async cancellationToken =>
            {
                await FastbootTcpServer.RunAsync(cancellationToken);
                return 0;
            },
            cancellationToken);

    internal static async Task<int> RunAsync(
        string[] args,
        Func<CancellationToken, Task<int>> runConsole,
        Func<CancellationToken, Task<int>> runFastbootTcp,
        CancellationToken cancellationToken)
    {
        var fastbootTcpOption = new Option<bool>("--fastboot-tcp")
        {
            Description = "Run the fastboot TCP service."
        };
        var root = new RootCommand("HomeHarbor recovery console.");
        root.Options.Add(fastbootTcpOption);
        root.SetAction(async (parseResult, actionCancellationToken) =>
        {
            return parseResult.GetValue(fastbootTcpOption)
                ? await runFastbootTcp(actionCancellationToken)
                : await runConsole(actionCancellationToken);
        });

        var parseResult = root.Parse(args);
        var exitCode = await parseResult.InvokeAsync(
            new InvocationConfiguration { EnableDefaultExceptionHandler = false },
            cancellationToken);
        return parseResult.Errors.Count > 0 && exitCode != 0 ? 2 : exitCode;
    }
}

internal static class RecoveryConsole
{
    public static async Task<int> RunAsync()
    {
        while (true)
        {
            Console.Clear();
            Console.WriteLine("HomeHarbor recovery");
            Console.WriteLine();
            Console.WriteLine("fastboot: tcp port " + Env.Int("HOMEHARBOR_FASTBOOTD_PORT", FastbootTcpServer.DefaultPort).ToString(CultureInfo.InvariantCulture));
            Console.WriteLine("state:     " + Env.String("HOMEHARBOR_RECOVERY_STATE_DIR", "/var/lib/homeharbor/recovery"));
            Console.WriteLine();
            Console.WriteLine("[s] status   [r] reboot   [n] normal boot   [q] redraw");

            var key = Console.ReadKey(intercept: true).KeyChar;
            switch (char.ToLowerInvariant(key))
            {
                case 's':
                    Console.WriteLine();
                    Console.WriteLine(await Command.RunCaptureAsync("systemctl", "is-active", "homeharbor-fastbootd.service"));
                    Pause();
                    break;
                case 'r':
                    _ = await Command.RunAsync("systemctl", "reboot");
                    return 0;
                case 'n':
                    HomeHarbor.Tooling.BootState.SetDefault("/efi", "A", "A");
                    _ = await Command.RunAsync("systemctl", "reboot");
                    return 0;
                default:
                    break;
            }
        }
    }

    private static void Pause()
    {
        Console.WriteLine();
        Console.WriteLine("Press any key to continue.");
        _ = Console.ReadKey(intercept: true);
    }
}

internal sealed class FastbootTcpServer
{
    public const int DefaultPort = 5554;

    public static async Task RunAsync(CancellationToken cancellationToken)
    {
        var listenAddress = Env.String("HOMEHARBOR_FASTBOOTD_LISTEN", "0.0.0.0");
        var port = Env.Int("HOMEHARBOR_FASTBOOTD_PORT", DefaultPort);
        var ipAddress = IPAddress.Parse(listenAddress);
        var listener = new TcpListener(ipAddress, port);

        listener.Start();
        Console.WriteLine($"HomeHarbor fastboot TCP service listening on {ipAddress}:{port}");

        while (!cancellationToken.IsCancellationRequested)
        {
            var client = await listener.AcceptTcpClientAsync(cancellationToken);
            _ = Task.Run(async () =>
            {
                try
                {
                    await using var session = new FastbootTcpSession(client);
                    await session.RunAsync(cancellationToken);
                }
                catch (Exception ex) when (ex is IOException or SocketException or EndOfStreamException)
                {
                    Console.Error.WriteLine("fastboot tcp session failed: " + ex.Message);
                }
            }, cancellationToken);
        }
    }

    private sealed class FastbootTcpSession(TcpClient client) : IAsyncDisposable
    {
        private const int ProtocolVersion = 1;
        private const int MaxCommandBytes = 4096;
        private static readonly byte[] HandshakeResponse = Encoding.ASCII.GetBytes("FB01");

        private readonly TcpClient _client = client;
        private readonly NetworkStream _stream = client.GetStream();
        private readonly FastbootActions _actions = new();
        private FileStream? _download;
        private long _downloadRemaining;

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            if (!await InitializeProtocolAsync(cancellationToken))
            {
                return;
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                var length = await ReadMessageLengthOrNullAsync(cancellationToken);
                if (length is null)
                {
                    return;
                }

                if (_downloadRemaining > 0)
                {
                    await HandleDownloadFrameAsync(length.Value, cancellationToken);
                    continue;
                }

                if (length.Value > MaxCommandBytes)
                {
                    await DrainAsync(length.Value, cancellationToken);
                    await WriteAsciiMessageAsync("FAILcommand too large", cancellationToken);
                    continue;
                }

                var payload = await ReadExactlyOrNullAsync((int)length.Value, cancellationToken);
                if (payload is null)
                {
                    return;
                }

                await HandleCommandAsync(payload, cancellationToken);
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_download is not null)
            {
                await _download.DisposeAsync();
            }

            _stream.Dispose();
            _client.Dispose();
        }

        private async Task<bool> InitializeProtocolAsync(CancellationToken cancellationToken)
        {
            var handshake = await ReadExactlyOrNullAsync(4, cancellationToken);
            if (handshake is null)
            {
                return false;
            }

            if (handshake[0] != (byte)'F' || handshake[1] != (byte)'B')
            {
                return false;
            }

            var versionText = Encoding.ASCII.GetString(handshake, 2, 2);
            if (!int.TryParse(versionText, CultureInfo.InvariantCulture, out var version) || version < ProtocolVersion)
            {
                return false;
            }

            await _stream.WriteAsync(HandshakeResponse, cancellationToken);
            return true;
        }

        private async Task HandleCommandAsync(byte[] payload, CancellationToken cancellationToken)
        {
            var command = Encoding.ASCII.GetString(payload).TrimEnd('\0', '\r', '\n');
            if (string.IsNullOrWhiteSpace(command))
            {
                return;
            }

            Console.WriteLine("fastboot-tcp: " + command);
            if (command.StartsWith("getvar:", StringComparison.Ordinal))
            {
                await WriteAsciiMessageAsync("OKAY" + _actions.GetVar(command["getvar:".Length..]), cancellationToken);
                return;
            }

            if (command.StartsWith("download:", StringComparison.Ordinal))
            {
                await BeginDownloadAsync(command["download:".Length..], cancellationToken);
                return;
            }

            if (command.StartsWith("flash:", StringComparison.Ordinal))
            {
                await WriteStatusAsync(await _actions.FlashAsync(command["flash:".Length..], cancellationToken), cancellationToken);
                return;
            }

            if (command.StartsWith("erase:", StringComparison.Ordinal))
            {
                await WriteStatusAsync(await _actions.EraseAsync(command["erase:".Length..], cancellationToken), cancellationToken);
                return;
            }

            if (command.StartsWith("set_active:", StringComparison.Ordinal))
            {
                await WriteStatusAsync(await _actions.SetActiveAsync(command["set_active:".Length..], cancellationToken), cancellationToken);
                return;
            }

            if (string.Equals(command, "reboot", StringComparison.Ordinal) || string.Equals(command, "reboot-recovery", StringComparison.Ordinal))
            {
                await WriteAsciiMessageAsync("OKAYrebooting", cancellationToken);
                await _actions.RebootAsync(command, cancellationToken);
                return;
            }

            await WriteAsciiMessageAsync("FAILunknown command", cancellationToken);
        }

        private async Task BeginDownloadAsync(string hexSize, CancellationToken cancellationToken)
        {
            if (!long.TryParse(hexSize, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var size) || size < 0)
            {
                await WriteAsciiMessageAsync("FAILinvalid download size", cancellationToken);
                return;
            }

            if (size > _actions.MaxDownloadBytes)
            {
                await WriteAsciiMessageAsync("FAILdownload too large", cancellationToken);
                return;
            }

            _ = Directory.CreateDirectory(Path.GetDirectoryName(_actions.DownloadPath)!);
            if (_download is not null)
            {
                await _download.DisposeAsync();
            }

            _download = File.Create(_actions.DownloadPath);
            _downloadRemaining = size;
            await WriteAsciiMessageAsync("DATA" + size.ToString("x8", CultureInfo.InvariantCulture), cancellationToken);
            if (size == 0)
            {
                await CompleteDownloadAsync(cancellationToken);
            }
        }

        private async Task HandleDownloadFrameAsync(ulong length, CancellationToken cancellationToken)
        {
            if (_download is null)
            {
                await DrainAsync(length, cancellationToken);
                await WriteAsciiMessageAsync("FAILno active download", cancellationToken);
                return;
            }

            if (length > (ulong)_downloadRemaining)
            {
                await DrainAsync(length, cancellationToken);
                await AbortDownloadAsync();
                await WriteAsciiMessageAsync("FAILdownload payload too large", cancellationToken);
                return;
            }

            var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
            try
            {
                var remaining = length;
                while (remaining > 0)
                {
                    var toRead = (int)Math.Min((ulong)buffer.Length, remaining);
                    var read = await _stream.ReadAsync(buffer.AsMemory(0, toRead), cancellationToken);
                    if (read == 0)
                    {
                        throw new EndOfStreamException("client disconnected during download");
                    }

                    await _download.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    _downloadRemaining -= read;
                    remaining -= (ulong)read;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            if (_downloadRemaining == 0)
            {
                await CompleteDownloadAsync(cancellationToken);
            }
        }

        private async Task CompleteDownloadAsync(CancellationToken cancellationToken)
        {
            await _download!.DisposeAsync();
            _download = null;
            await WriteAsciiMessageAsync("OKAYdownloaded", cancellationToken);
        }

        private async Task AbortDownloadAsync()
        {
            if (_download is not null)
            {
                await _download.DisposeAsync();
                _download = null;
            }

            _downloadRemaining = 0;
            if (File.Exists(_actions.DownloadPath))
            {
                File.Delete(_actions.DownloadPath);
            }
        }

        private Task WriteStatusAsync(FastbootStatus status, CancellationToken cancellationToken) =>
            WriteAsciiMessageAsync((status.Ok ? "OKAY" : "FAIL") + status.Message, cancellationToken);

        private Task WriteAsciiMessageAsync(string value, CancellationToken cancellationToken) =>
            WriteMessageAsync(Encoding.ASCII.GetBytes(value), cancellationToken);

        private async Task WriteMessageAsync(byte[] payload, CancellationToken cancellationToken)
        {
            var header = new byte[8];
            BinaryPrimitives.WriteUInt64BigEndian(header, (ulong)payload.Length);
            await _stream.WriteAsync(header, cancellationToken);
            if (payload.Length > 0)
            {
                await _stream.WriteAsync(payload, cancellationToken);
            }
        }

        private async Task<ulong?> ReadMessageLengthOrNullAsync(CancellationToken cancellationToken)
        {
            var header = await ReadExactlyOrNullAsync(8, cancellationToken);
            return header is null ? null : BinaryPrimitives.ReadUInt64BigEndian(header);
        }

        private async Task<byte[]?> ReadExactlyOrNullAsync(int length, CancellationToken cancellationToken)
        {
            var buffer = new byte[length];
            var offset = 0;
            while (offset < length)
            {
                var read = await _stream.ReadAsync(buffer.AsMemory(offset, length - offset), cancellationToken);
                if (read == 0)
                {
                    return offset == 0 ? null : throw new EndOfStreamException("client disconnected mid-packet");
                }

                offset += read;
            }

            return buffer;
        }

        private async Task DrainAsync(ulong length, CancellationToken cancellationToken)
        {
            var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
            try
            {
                var remaining = length;
                while (remaining > 0)
                {
                    var toRead = (int)Math.Min((ulong)buffer.Length, remaining);
                    var read = await _stream.ReadAsync(buffer.AsMemory(0, toRead), cancellationToken);
                    if (read == 0)
                    {
                        throw new EndOfStreamException("client disconnected mid-packet");
                    }

                    remaining -= (ulong)read;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }
}

internal sealed class FastbootActions
{
    private static readonly IReadOnlyDictionary<string, PartitionInfo> Partitions = new Dictionary<string, PartitionInfo>(StringComparer.Ordinal)
    {
        ["root_a"] = new(2304L * 1024 * 1024, "avb-erofs", true, true),
        ["root_b"] = new(2304L * 1024 * 1024, "avb-erofs", true, true),
        ["modules_a"] = new(448L * 1024 * 1024, "avb-erofs", true, true),
        ["modules_b"] = new(448L * 1024 * 1024, "avb-erofs", true, true),
        ["firmware_a"] = new(832L * 1024 * 1024, "avb-erofs", true, true),
        ["firmware_b"] = new(832L * 1024 * 1024, "avb-erofs", true, true),
        ["boot_a"] = new(512L * 1024 * 1024, "raw-uki", true, true, "boot_a"),
        ["boot_b"] = new(512L * 1024 * 1024, "raw-uki", true, true, "boot_b"),
        ["recovery_a"] = new(1648L * 1024 * 1024, "avb-erofs", true, true, "recovery_a"),
        ["recovery_b"] = new(1648L * 1024 * 1024, "avb-erofs", true, true, "recovery_b"),
        ["vbmeta_a"] = new(16L * 1024 * 1024, "avb-vbmeta", true, true, "vbmeta_a"),
        ["vbmeta_b"] = new(16L * 1024 * 1024, "avb-vbmeta", true, true, "vbmeta_b"),
        ["super"] = new(7184L * 1024 * 1024, "android-super", true, false),
    };

    public string StateDir { get; } = Env.String("HOMEHARBOR_RECOVERY_STATE_DIR", "/var/lib/homeharbor/recovery");
    public string DownloadPath => Path.Combine(StateDir, "download.img");
    public long MaxDownloadBytes { get; } = Env.Long("HOMEHARBOR_FASTBOOTD_MAX_DOWNLOAD_BYTES", 4L * 1024 * 1024 * 1024);

    private string SuperDevice { get; } = Env.String("HOMEHARBOR_FASTBOOTD_SUPER_DEVICE", "/dev/disk/by-partlabel/super");
    private string EspPath { get; } = "/efi";
    private bool DryRun { get; } = Env.Bool("HOMEHARBOR_FASTBOOTD_DRY_RUN");
    private readonly HomeHarbor.Tooling.ICommandRunner _runner;
    private readonly HomeHarbor.Tooling.SuperMapper _superMapper;

    public FastbootActions()
    {
        _runner = new HomeHarbor.Tooling.ProcessCommandRunner();
        _superMapper = new HomeHarbor.Tooling.SuperMapper(_runner);
    }

    public string GetVar(string name)
    {
        if (name.StartsWith("partition-size:", StringComparison.Ordinal))
        {
            var partition = name["partition-size:".Length..];
            return Partitions.TryGetValue(partition, out var info)
                ? "0x" + info.SizeBytes.ToString("x", CultureInfo.InvariantCulture)
                : "";
        }

        if (name.StartsWith("partition-type:", StringComparison.Ordinal))
        {
            var partition = name["partition-type:".Length..];
            return Partitions.TryGetValue(partition, out var info) ? info.Type : "";
        }

        return name switch
        {
            "all" => "product:HomeHarbor\nversion:0.4\nis-userspace:yes\nslot-count:2\nsecure:no\nunlocked:yes",
            "product" => "HomeHarbor",
            "version" => "0.4",
            "is-userspace" => "yes",
            "slot-count" => "2",
            "current-slot" => ReadBootEnvValue("HOMEHARBOR_BOOT_SLOT").ToLowerInvariant(),
            "secure" => "no",
            "unlocked" => "yes",
            "max-download-size" => "0x" + MaxDownloadBytes.ToString("x", CultureInfo.InvariantCulture),
            _ => "",
        };
    }

    public async Task<FastbootStatus> FlashAsync(string partition, CancellationToken cancellationToken)
    {
        if (!CanFlash(partition, out var failure))
        {
            return FastbootStatus.Fail(failure);
        }

        if (!File.Exists(DownloadPath))
        {
            return FastbootStatus.Fail("no downloaded image");
        }

        var payloadSize = new FileInfo(DownloadPath).Length;
        var info = Partitions[partition];
        if (payloadSize > info.SizeBytes)
        {
            return FastbootStatus.Fail("image is larger than partition");
        }

        _ = Directory.CreateDirectory(StateDir);
        if (DryRun)
        {
            await File.AppendAllTextAsync(Path.Combine(StateDir, "fastbootd-dry-run.log"),
                $"flash {partition} {payloadSize}{Environment.NewLine}", cancellationToken);
            return FastbootStatus.Okay("dry-run flashed " + partition);
        }

        return partition == "super"
            ? !Env.Bool("HOMEHARBOR_FASTBOOTD_ALLOW_RAW_SUPER")
                ? FastbootStatus.Fail("raw super flashing disabled")
                : await RunShellFlashAsync(SuperDevice, partition, cancellationToken)
            : info.PhysicalLabel is null
            ? await FlashLogicalPartitionAsync(partition, cancellationToken)
            : await FlashPhysicalPartitionAsync(info.PhysicalLabel, partition, cancellationToken);
    }

    public async Task<FastbootStatus> EraseAsync(string partition, CancellationToken cancellationToken)
    {
        if (!CanFlash(partition, out var failure) || partition == "super")
        {
            return FastbootStatus.Fail(failure == "ok" ? "erase not allowed" : failure);
        }

        if (DryRun)
        {
            _ = Directory.CreateDirectory(StateDir);
            await File.AppendAllTextAsync(Path.Combine(StateDir, "fastbootd-dry-run.log"),
                $"erase {partition}{Environment.NewLine}", cancellationToken);
            return FastbootStatus.Okay("dry-run erased " + partition);
        }

        var info = Partitions[partition];
        return info.PhysicalLabel is null
            ? await EraseLogicalPartitionAsync(partition, cancellationToken)
            : await ErasePhysicalPartitionAsync(info.PhysicalLabel, partition, cancellationToken);
    }

    public async Task<FastbootStatus> SetActiveAsync(string slot, CancellationToken cancellationToken)
    {
        var normalized = slot.TrimStart('_').ToLowerInvariant();
        if (normalized is not ("a" or "b"))
        {
            return FastbootStatus.Fail("slot must be a or b");
        }

        if (DryRun)
        {
            _ = Directory.CreateDirectory(StateDir);
            await File.AppendAllTextAsync(Path.Combine(StateDir, "fastbootd-dry-run.log"),
                $"set_active {normalized}{Environment.NewLine}", cancellationToken);
            return FastbootStatus.Okay("dry-run active slot " + normalized);
        }

        HomeHarbor.Tooling.BootState.SetDefault(EspPath, normalized.ToUpperInvariant(), normalized.ToUpperInvariant());
        return FastbootStatus.Okay("active slot " + normalized);
    }

    public async Task RebootAsync(string target, CancellationToken cancellationToken)
    {
        if (DryRun)
        {
            _ = Directory.CreateDirectory(StateDir);
            await File.AppendAllTextAsync(Path.Combine(StateDir, "fastbootd-dry-run.log"),
                $"{target}{Environment.NewLine}", cancellationToken);
            return;
        }

        if (target == "reboot-recovery")
        {
            var state = HomeHarbor.Tooling.BootState.Read(EspPath);
            await HomeHarbor.Tooling.EfiBootVariables.SetOneShotAsync(
                new HomeHarbor.Tooling.ProcessCommandRunner(),
                state.RecoverySlot,
                state.RecoverySlot,
                "recovery",
                cancellationToken);
        }

        _ = await Command.RunAsync("systemctl", "reboot");
    }

    private static bool CanFlash(string partition, out string failure)
    {
        if (!Partitions.TryGetValue(partition, out var info))
        {
            failure = "partition is not flashable";
            return false;
        }

        if (!info.Flashable)
        {
            failure = "partition is not flashable";
            return false;
        }

        failure = "ok";
        return true;
    }

    private async Task<FastbootStatus> FlashLogicalPartitionAsync(string partition, CancellationToken cancellationToken)
    {
        var mapName = "homeharbor-fastboot-" + partition.Replace('_', '-') + "-" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
        var mapperPath = "/dev/mapper/" + mapName;

        try
        {
            await _superMapper.CreateAsync(mapName, SuperDevice, partition, "rw", cancellationToken);
            var sizeResult = await _runner.RunAsync("blockdev", ["--getsize64", mapperPath], cancellationToken: cancellationToken);
            _ = sizeResult.EnsureSuccess("blockdev failed");

            var size = long.Parse(sizeResult.Stdout.Trim(), CultureInfo.InvariantCulture);
            var payloadSize = new FileInfo(DownloadPath).Length;
            if (payloadSize > size)
            {
                return FastbootStatus.Fail("image is larger than mapped partition");
            }

            var result = await _runner.RunAsync(
                "dd",
                [$"if={DownloadPath}", $"of={mapperPath}", "bs=4M", "conv=fsync,notrunc", "status=none"],
                new HomeHarbor.Tooling.CommandRunOptions(StreamOutput: true, StreamError: true),
                cancellationToken);
            return result.ExitCode == 0 ? FastbootStatus.Okay("flashed " + partition) : FastbootStatus.Fail("flash failed");
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or FormatException)
        {
            return FastbootStatus.Fail("flash failed: " + ex.Message);
        }
        finally
        {
            await _superMapper.RemoveAsync(mapName, CancellationToken.None);
        }
    }

    private async Task<FastbootStatus> FlashPhysicalPartitionAsync(string label, string partition, CancellationToken cancellationToken)
    {
        var device = "/dev/disk/by-partlabel/" + label;
        return await RunShellFlashAsync(device, partition, cancellationToken);
    }

    private async Task<FastbootStatus> EraseLogicalPartitionAsync(string partition, CancellationToken cancellationToken)
    {
        var mapName = "homeharbor-fastboot-erase-" + partition.Replace('_', '-') + "-" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
        var mapperPath = "/dev/mapper/" + mapName;

        try
        {
            await _superMapper.CreateAsync(mapName, SuperDevice, partition, "rw", cancellationToken);
            _ = await _runner.RunAsync(
                "dd",
                ["if=/dev/zero", $"of={mapperPath}", "bs=4M", "status=none"],
                new HomeHarbor.Tooling.CommandRunOptions(StreamOutput: true, StreamError: true),
                cancellationToken);
            var flush = await _runner.RunAsync("blockdev", ["--flushbufs", mapperPath], cancellationToken: cancellationToken);
            return flush.ExitCode == 0 ? FastbootStatus.Okay("erased " + partition) : FastbootStatus.Fail("erase failed");
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            return FastbootStatus.Fail("erase failed: " + ex.Message);
        }
        finally
        {
            await _superMapper.RemoveAsync(mapName, CancellationToken.None);
        }
    }

    private async Task<FastbootStatus> ErasePhysicalPartitionAsync(string label, string partition, CancellationToken cancellationToken)
    {
        var device = "/dev/disk/by-partlabel/" + label;
        var result = await _runner.RunAsync(
            "dd",
            ["if=/dev/zero", $"of={device}", "bs=4M", "status=none"],
            new HomeHarbor.Tooling.CommandRunOptions(StreamOutput: true, StreamError: true),
            cancellationToken);
        if (result.ExitCode != 0)
        {
            return FastbootStatus.Fail("erase failed");
        }

        var flush = await _runner.RunAsync("blockdev", ["--flushbufs", device], cancellationToken: cancellationToken);
        return flush.ExitCode == 0 ? FastbootStatus.Okay("erased " + partition) : FastbootStatus.Fail("erase failed");
    }

    private async Task<FastbootStatus> RunShellFlashAsync(string targetDevice, string partition, CancellationToken cancellationToken)
    {
        var sizeResult = await _runner.RunAsync("blockdev", ["--getsize64", targetDevice], cancellationToken: cancellationToken);
        if (sizeResult.ExitCode != 0)
        {
            return FastbootStatus.Fail("flash failed");
        }

        var size = long.Parse(sizeResult.Stdout.Trim(), CultureInfo.InvariantCulture);
        var payloadSize = new FileInfo(DownloadPath).Length;
        if (payloadSize > size)
        {
            return FastbootStatus.Fail("image is larger than " + partition);
        }

        var result = await _runner.RunAsync(
            "dd",
            [$"if={DownloadPath}", $"of={targetDevice}", "bs=4M", "conv=fsync,notrunc", "status=none"],
            new HomeHarbor.Tooling.CommandRunOptions(StreamOutput: true, StreamError: true),
            cancellationToken);
        return result.ExitCode == 0 ? FastbootStatus.Okay("flashed " + partition) : FastbootStatus.Fail("flash failed");
    }

    private static string ReadBootEnvValue(string key)
    {
        foreach (var path in new[] { "/run/homeharbor/boot.env", "/var/lib/homeharbor/ota/current.env" })
        {
            if (!File.Exists(path))
            {
                continue;
            }

            foreach (var line in File.ReadLines(path))
            {
                var equals = line.IndexOf('=');
                if (equals > 0 && string.Equals(line[..equals], key, StringComparison.Ordinal))
                {
                    return line[(equals + 1)..];
                }
            }
        }

        return "unknown";
    }

    private sealed record PartitionInfo(long SizeBytes, string Type, bool Flashable, bool Erasable, string? PhysicalLabel = null);
}

internal readonly record struct FastbootStatus(bool Ok, string Message)
{
    public static FastbootStatus Okay(string message) => new(true, message);
    public static FastbootStatus Fail(string message) => new(false, message);
}

internal static class Command
{
    public static async Task<int> RunAsync(string fileName, params string[] args)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("failed to start " + fileName);
        await process.WaitForExitAsync();
        return process.ExitCode;
    }

    public static async Task<string> RunCaptureAsync(string fileName, params string[] args)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("failed to start " + fileName);
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return string.IsNullOrWhiteSpace(output) ? error.Trim() : output.Trim();
    }

}

internal static class Env
{
    public static string String(string name, string fallback)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    public static int Int(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;

    public static long Long(string name, long fallback) =>
        long.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;

    public static bool Bool(string name) =>
        string.Equals(Environment.GetEnvironmentVariable(name), "1", StringComparison.Ordinal) ||
        string.Equals(Environment.GetEnvironmentVariable(name), "true", StringComparison.OrdinalIgnoreCase);
}
