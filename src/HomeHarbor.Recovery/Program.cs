using System.Buffers;
using System.Buffers.Binary;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
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
            static (path, cancellationToken) => RecoveryPrivilegedAction.ApplyAsync(
                path,
                new HomeHarbor.Tooling.ProcessCommandRunner(),
                cancellationToken),
            cancellationToken);

    internal static async Task<int> RunAsync(
        string[] args,
        Func<CancellationToken, Task<int>> runConsole,
        Func<CancellationToken, Task<int>> runFastbootTcp,
        CancellationToken cancellationToken)
        => await RunAsync(
            args,
            runConsole,
            runFastbootTcp,
            static (path, cancellationToken) => RecoveryPrivilegedAction.ApplyAsync(
                path,
                new HomeHarbor.Tooling.ProcessCommandRunner(),
                cancellationToken),
            cancellationToken);

    private static async Task<int> RunAsync(
        string[] args,
        Func<CancellationToken, Task<int>> runConsole,
        Func<CancellationToken, Task<int>> runFastbootTcp,
        Func<string, CancellationToken, Task<int>> applyPrivilegedAction,
        CancellationToken cancellationToken)
    {
        var fastbootTcpOption = new Option<bool>("--fastboot-tcp")
        {
            Description = "Run the fastboot TCP service."
        };
        var fastbootAuthProxyOption = new Option<string?>("--fastboot-auth-proxy")
        {
            Description = "Connect an authenticated appliance fastboot session to a one-shot loopback proxy for the stock fastboot client."
        };
        var applyActionOption = new Option<string?>("--apply-action")
        {
            Description = "Apply a fixed recovery-console action request (system service use only)."
        };
        var root = new RootCommand("HomeHarbor recovery console.");
        root.Options.Add(fastbootTcpOption);
        root.Options.Add(fastbootAuthProxyOption);
        root.Options.Add(applyActionOption);
        root.SetAction(async (parseResult, actionCancellationToken) =>
        {
            var actionPath = parseResult.GetValue(applyActionOption);
            if (!string.IsNullOrWhiteSpace(actionPath))
            {
                return await applyPrivilegedAction(actionPath, actionCancellationToken);
            }

            var proxyTarget = parseResult.GetValue(fastbootAuthProxyOption);
            if (!string.IsNullOrWhiteSpace(proxyTarget))
            {
                return await FastbootAuthenticatedProxy.RunAsync(proxyTarget, actionCancellationToken);
            }

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
        var unlockGate = FastbootUnlockGate.FromEnvironment();
        while (true)
        {
            Console.Clear();
            Console.WriteLine("HomeHarbor recovery");
            Console.WriteLine();
            Console.WriteLine("fastboot: tcp port " + Env.Int("HOMEHARBOR_FASTBOOTD_PORT", FastbootTcpServer.DefaultPort).ToString(CultureInfo.InvariantCulture));
            Console.WriteLine(unlockGate.IsUnlocked(out var remaining)
                ? "access:   physical authorization window open for " + Math.Ceiling(remaining.TotalMinutes).ToString(CultureInfo.InvariantCulture) + " minute(s)"
                : "access:   destructive fastboot locked");
            Console.WriteLine("state:     " + Env.String("HOMEHARBOR_RECOVERY_STATE_DIR", "/var/lib/homeharbor/recovery"));
            Console.WriteLine();
            Console.WriteLine("[u] unlock fastboot   [l] lock fastboot   [s] status   [r] reboot   [n] normal boot   [q] redraw");

            var key = Console.ReadKey(intercept: true).KeyChar;
            switch (char.ToLowerInvariant(key))
            {
                case 's':
                    Console.WriteLine();
                    Console.WriteLine(await Command.RunCaptureAsync("systemctl", "is-active", "homeharbor-fastbootd.service"));
                    Pause();
                    break;
                case 'u':
                    Console.WriteLine();
                    Console.Write("Type UNLOCK to create a one-session fastboot authorization for 10 minutes: ");
                    if (string.Equals(Console.ReadLine(), "UNLOCK", StringComparison.Ordinal))
                    {
                        var grant = unlockGate.Grant(FastbootUnlockGate.DefaultDuration);
                        Console.WriteLine("Fastboot authorization token (shown once):");
                        Console.WriteLine(grant.AuthorizationToken);
                        Console.WriteLine();
                        Console.WriteLine("On the trusted workstation, enter this token into the HomeHarbor fastboot authenticated proxy.");
                        Console.WriteLine("The token is valid for one TCP session until " + grant.ExpiresAt.ToLocalTime().ToString("T", CultureInfo.CurrentCulture) + ".");
                    }
                    else
                    {
                        Console.WriteLine("Fastboot remains locked.");
                    }

                    Pause();
                    break;
                case 'l':
                    unlockGate.Revoke();
                    Console.WriteLine();
                    Console.WriteLine("Fastboot locked.");
                    Pause();
                    break;
                case 'r':
                    RequestPrivilegedAction("reboot");
                    return 0;
                case 'n':
                    RequestPrivilegedAction("normal");
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

    private static void RequestPrivilegedAction(string action)
    {
        var path = Env.String("HOMEHARBOR_RECOVERY_ACTION_REQUEST", "/run/homeharbor-recovery/action.request");
        var directory = Path.GetDirectoryName(path) ?? ".";
        _ = Directory.CreateDirectory(directory);
        var temp = path + ".tmp." + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + "." + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temp, action + Environment.NewLine, new UTF8Encoding(false));
            File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }
}

internal static class FastbootAuthenticatedProxy
{
    public const int DefaultLocalPort = 5555;
    private const int TokenLength = 64;
    private const int MaximumResponseBytes = 4096;
    private static readonly byte[] Handshake = Encoding.ASCII.GetBytes("FB01");
    private static readonly byte[] AuthorizationPrefix = Encoding.ASCII.GetBytes("oem auth ");

    public static async Task<int> RunAsync(string target, CancellationToken cancellationToken)
    {
        if (!IPAddress.TryParse(target, out var targetAddress))
        {
            throw new ArgumentException("fastboot proxy target must be a numeric IP address", nameof(target));
        }

        var remotePort = Math.Clamp(Env.Int("HOMEHARBOR_FASTBOOTD_PORT", FastbootTcpServer.DefaultPort), 1, 65535);
        var localPort = Math.Clamp(Env.Int("HOMEHARBOR_FASTBOOT_PROXY_PORT", DefaultLocalPort), 1024, 65535);
        var token = ReadToken();
        try
        {
            using var remoteClient = new TcpClient(targetAddress.AddressFamily);
            await remoteClient.ConnectAsync(targetAddress, remotePort, cancellationToken);
            await using var remote = remoteClient.GetStream();
            await AuthenticateAsync(remote, token, cancellationToken);

            var listener = new TcpListener(IPAddress.Loopback, localPort);
            listener.Start(1);
            try
            {
                Console.WriteLine($"Authenticated proxy ready at tcp:127.0.0.1:{localPort} for one fastboot operation.");
                Console.WriteLine("Keep this process running, then point the stock fastboot client at that loopback address.");
                using var acceptTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                acceptTimeout.CancelAfter(TimeSpan.FromSeconds(90));
                using var localClient = await listener.AcceptTcpClientAsync(acceptTimeout.Token);
                await using var local = localClient.GetStream();
                await AcceptLocalHandshakeAsync(local, cancellationToken);
                await ProxyAsync(local, remote, cancellationToken);
                return 0;
            }
            finally
            {
                listener.Stop();
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(token);
        }
    }

    internal static async Task AuthenticateAsync(Stream stream, byte[] token, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(token);
        if (token.Length != TokenLength || token.Any(value => !Uri.IsHexDigit((char)value)))
        {
            throw new ArgumentException("fastboot authorization token must contain exactly 64 hexadecimal characters", nameof(token));
        }

        await stream.WriteAsync(Handshake, cancellationToken);
        var handshake = await ReadExactlyAsync(stream, Handshake.Length, cancellationToken);
        if (!handshake.AsSpan().SequenceEqual(Handshake))
        {
            throw new IOException("remote fastboot handshake failed");
        }

        var command = new byte[AuthorizationPrefix.Length + token.Length];
        try
        {
            AuthorizationPrefix.CopyTo(command, 0);
            token.CopyTo(command, AuthorizationPrefix.Length);
            await WriteMessageAsync(stream, command, cancellationToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(command);
        }

        var response = await ReadMessageAsync(stream, cancellationToken);
        try
        {
            if (!response.AsSpan().StartsWith("OKAY"u8))
            {
                throw new UnauthorizedAccessException("remote fastboot session authorization failed");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(response);
        }
    }

    private static byte[] ReadToken()
    {
        if (Console.IsInputRedirected)
        {
            throw new InvalidOperationException("fastboot proxy token must be entered at an interactive terminal");
        }

        Console.Write("Fastboot authorization token (input hidden): ");
        var token = new byte[TokenLength];
        var length = 0;
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                break;
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (length > 0)
                {
                    token[--length] = 0;
                }

                continue;
            }

            var value = key.KeyChar;
            if (length >= TokenLength || !Uri.IsHexDigit(value) || value > 127)
            {
                CryptographicOperations.ZeroMemory(token);
                throw new InvalidOperationException("fastboot authorization token must contain exactly 64 hexadecimal characters");
            }

            token[length++] = (byte)value;
        }

        if (length != TokenLength)
        {
            CryptographicOperations.ZeroMemory(token);
            throw new InvalidOperationException("fastboot authorization token must contain exactly 64 hexadecimal characters");
        }

        return token;
    }

    private static async Task AcceptLocalHandshakeAsync(Stream stream, CancellationToken cancellationToken)
    {
        var handshake = await ReadExactlyAsync(stream, Handshake.Length, cancellationToken);
        if (handshake[0] != (byte)'F' || handshake[1] != (byte)'B')
        {
            throw new IOException("local fastboot handshake failed");
        }

        await stream.WriteAsync(Handshake, cancellationToken);
    }

    private static async Task ProxyAsync(Stream local, Stream remote, CancellationToken cancellationToken)
    {
        using var proxyCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var upload = local.CopyToAsync(remote, proxyCancellation.Token);
        var download = remote.CopyToAsync(local, proxyCancellation.Token);
        _ = await Task.WhenAny(upload, download);
        await proxyCancellation.CancelAsync();
        try
        {
            await Task.WhenAll(upload, download);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
        }
    }

    private static async Task WriteMessageAsync(Stream stream, byte[] payload, CancellationToken cancellationToken)
    {
        var header = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(header, (ulong)payload.Length);
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
    }

    private static async Task<byte[]> ReadMessageAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = await ReadExactlyAsync(stream, 8, cancellationToken);
        var length = BinaryPrimitives.ReadUInt64BigEndian(header);
        if (length > MaximumResponseBytes)
        {
            throw new IOException("remote fastboot response is too large");
        }

        return await ReadExactlyAsync(stream, (int)length, cancellationToken);
    }

    private static async Task<byte[]> ReadExactlyAsync(Stream stream, int length, CancellationToken cancellationToken)
    {
        var buffer = new byte[length];
        var offset = 0;
        while (offset < length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken);
            if (read == 0)
            {
                throw new EndOfStreamException("fastboot connection closed unexpectedly");
            }

            offset += read;
        }

        return buffer;
    }
}

internal static class RecoveryPrivilegedAction
{
    public static async Task<int> ApplyAsync(
        string requestPath,
        HomeHarbor.Tooling.ICommandRunner runner,
        CancellationToken cancellationToken)
    {
        var attributes = File.GetAttributes(requestPath);
        if (attributes.HasFlag(FileAttributes.Directory) || attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidOperationException("recovery action request must be a regular file");
        }

        if (new FileInfo(requestPath).Length > 32)
        {
            throw new InvalidOperationException("recovery action request is too large");
        }

        var action = (await File.ReadAllTextAsync(requestPath, cancellationToken)).Trim();
        switch (action)
        {
            case "normal":
            case "reboot":
                break;
            default:
                throw new InvalidOperationException("unsupported recovery action: " + action);
        }

        File.Delete(requestPath);
        _ = (await runner.RunAsync("systemctl", ["reboot"], cancellationToken: cancellationToken))
            .EnsureSuccess("recovery reboot request failed");
        return 0;
    }
}

internal sealed class FastbootTcpServer
{
    public const int DefaultPort = 5554;
    private static readonly SemaphoreSlim SessionGate = new(1, 1);
    private static readonly byte[] AuthorizationCommandStem = Encoding.ASCII.GetBytes("oem auth");
    private static readonly byte[] AuthorizationCommandPrefix = Encoding.ASCII.GetBytes("oem auth ");

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
            if (!await SessionGate.WaitAsync(0, cancellationToken))
            {
                client.Dispose();
                continue;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await using var session = new FastbootTcpSession(client);
                    await session.RunAsync(cancellationToken);
                }
                catch (Exception ex) when (ex is IOException or SocketException or EndOfStreamException or OperationCanceledException)
                {
                    Console.Error.WriteLine("fastboot tcp session failed: " + ex.Message);
                }
                finally
                {
                    SessionGate.Release();
                }
            }, CancellationToken.None);
        }
    }

    internal static bool TryHandleAuthorizationCommand(
        byte[] payload,
        FastbootActions actions,
        out FastbootStatus status)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(actions);
        var length = payload.Length;
        while (length > 0 && payload[length - 1] is 0 or (byte)'\r' or (byte)'\n')
        {
            length--;
        }

        var command = payload.AsSpan(0, length);
        if (!command.StartsWith(AuthorizationCommandStem))
        {
            status = default;
            return false;
        }

        try
        {
            status = command.StartsWith(AuthorizationCommandPrefix)
                ? actions.AuthenticateSession(command[AuthorizationCommandPrefix.Length..])
                : FastbootStatus.Fail("session authorization failed");
            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    internal static string CommandNameForLog(string command)
    {
        if (string.IsNullOrEmpty(command))
        {
            return "empty";
        }

        var separator = command.IndexOfAny([':', ' ']);
        var name = separator < 0 ? command : command[..separator];
        return name is "getvar" or "download" or "flash" or "erase" or "set_active" or "reboot" or "reboot-recovery" or "oem"
            ? name
            : "unknown";
    }

    private sealed class FastbootTcpSession(TcpClient client) : IAsyncDisposable
    {
        private const int ProtocolVersion = 1;
        private const int MaxCommandBytes = 4096;
        private const int DefaultLockedIdleTimeoutSeconds = 15;
        private const int DefaultUnlockedIdleTimeoutSeconds = 120;
        private const int DefaultLockedSessionLifetimeSeconds = 30;
        private const int DefaultUnlockedSessionLifetimeSeconds = 660;
        private static readonly byte[] HandshakeResponse = Encoding.ASCII.GetBytes("FB01");

        private readonly TcpClient _client = client;
        private readonly NetworkStream _stream = client.GetStream();
        private readonly FastbootActions _actions = new();
        private readonly long _sessionStarted = Stopwatch.GetTimestamp();
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
            try
            {
                if (_download is not null)
                {
                    await _download.DisposeAsync();
                }
            }
            finally
            {
                try
                {
                    _stream.Dispose();
                    _client.Dispose();
                }
                finally
                {
                    _actions.CleanupSession();
                }
            }
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
            if (TryHandleAuthorizationCommand(payload, _actions, out var authorizationStatus))
            {
                Console.WriteLine("fastboot-tcp: oem auth [redacted]");
                await WriteStatusAsync(authorizationStatus, cancellationToken);
                return;
            }

            var command = Encoding.ASCII.GetString(payload).TrimEnd('\0', '\r', '\n');
            CryptographicOperations.ZeroMemory(payload);
            if (string.IsNullOrWhiteSpace(command))
            {
                return;
            }

            Console.WriteLine("fastboot-tcp: " + CommandNameForLog(command));
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
                var partition = command["flash:".Length..];
                await WriteStatusAsync(
                    await _actions.RunAuthorizedOperationAsync(
                        operationCancellationToken => _actions.FlashAsync(partition, operationCancellationToken),
                        cancellationToken),
                    cancellationToken);
                return;
            }

            if (command.StartsWith("erase:", StringComparison.Ordinal))
            {
                var partition = command["erase:".Length..];
                await WriteStatusAsync(
                    await _actions.RunAuthorizedOperationAsync(
                        operationCancellationToken => _actions.EraseAsync(partition, operationCancellationToken),
                        cancellationToken),
                    cancellationToken);
                return;
            }

            if (command.StartsWith("set_active:", StringComparison.Ordinal))
            {
                await WriteStatusAsync(await _actions.SetActiveAsync(command["set_active:".Length..], cancellationToken), cancellationToken);
                return;
            }

            if (string.Equals(command, "reboot", StringComparison.Ordinal) || string.Equals(command, "reboot-recovery", StringComparison.Ordinal))
            {
                if (!_actions.DestructiveActionsAllowed(out var failure))
                {
                    await WriteAsciiMessageAsync("FAIL" + failure, cancellationToken);
                    return;
                }

                await WriteAsciiMessageAsync("OKAYrebooting", cancellationToken);
                await _actions.RebootAsync(command, cancellationToken);
                return;
            }

            await WriteAsciiMessageAsync("FAILunknown command", cancellationToken);
        }

        private async Task BeginDownloadAsync(string hexSize, CancellationToken cancellationToken)
        {
            if (!_actions.DestructiveActionsAllowed(out var failure))
            {
                await WriteAsciiMessageAsync("FAIL" + failure, cancellationToken);
                return;
            }

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

            _download = new FileStream(_actions.DownloadPath, new FileStreamOptions
            {
                Access = FileAccess.Write,
                Mode = FileMode.CreateNew,
                Share = FileShare.None,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
            });
            _downloadRemaining = size;
            await WriteAsciiMessageAsync("DATA" + size.ToString("x8", CultureInfo.InvariantCulture), cancellationToken);
            if (size == 0)
            {
                await CompleteDownloadAsync(cancellationToken);
            }
        }

        private async Task HandleDownloadFrameAsync(ulong length, CancellationToken cancellationToken)
        {
            if (!_actions.DestructiveActionsAllowed(out _))
            {
                await AbortDownloadAsync();
                throw new IOException("fastboot authorization expired or was revoked during download");
            }

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
                    if (!_actions.DestructiveActionsAllowed(out _))
                    {
                        await AbortDownloadAsync();
                        throw new IOException("fastboot authorization expired or was revoked during download");
                    }

                    var toRead = (int)Math.Min((ulong)buffer.Length, remaining);
                    var read = await ReadWithIdleTimeoutAsync(buffer.AsMemory(0, toRead), cancellationToken);
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
                var read = await ReadWithIdleTimeoutAsync(buffer.AsMemory(offset, length - offset), cancellationToken);
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
                    var read = await ReadWithIdleTimeoutAsync(buffer.AsMemory(0, toRead), cancellationToken);
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

        private async ValueTask<int> ReadWithIdleTimeoutAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var authorized = _actions.DestructiveActionsAllowed(out _);
            var configured = authorized
                ? Env.Int("HOMEHARBOR_FASTBOOTD_UNLOCKED_IDLE_TIMEOUT_SECONDS", DefaultUnlockedIdleTimeoutSeconds)
                : Env.Int("HOMEHARBOR_FASTBOOTD_LOCKED_IDLE_TIMEOUT_SECONDS", DefaultLockedIdleTimeoutSeconds);
            var seconds = Math.Clamp(configured, 5, 300);
            var lifetimeConfigured = authorized
                ? Env.Int("HOMEHARBOR_FASTBOOTD_UNLOCKED_SESSION_LIFETIME_SECONDS", DefaultUnlockedSessionLifetimeSeconds)
                : Env.Int("HOMEHARBOR_FASTBOOTD_LOCKED_SESSION_LIFETIME_SECONDS", DefaultLockedSessionLifetimeSeconds);
            var lifetime = TimeSpan.FromSeconds(Math.Clamp(lifetimeConfigured, 10, 3600));
            var elapsed = Stopwatch.GetElapsedTime(_sessionStarted);
            var sessionRemaining = lifetime - elapsed;
            if (sessionRemaining <= TimeSpan.Zero)
            {
                throw new IOException("fastboot tcp session lifetime expired");
            }

            timeout.CancelAfter(TimeSpan.FromSeconds(seconds) < sessionRemaining
                ? TimeSpan.FromSeconds(seconds)
                : sessionRemaining);
            try
            {
                return await _stream.ReadAsync(buffer, timeout.Token);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                throw new IOException("fastboot tcp session idle timeout", ex);
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
        ["firmware_a"] = new(128L * 1024 * 1024, "avb-erofs", true, true),
        ["firmware_b"] = new(128L * 1024 * 1024, "avb-erofs", true, true),
        ["boot_a"] = new(512L * 1024 * 1024, "raw-uki", true, true, "boot_a"),
        ["boot_b"] = new(512L * 1024 * 1024, "raw-uki", true, true, "boot_b"),
        ["recovery_a"] = new(1648L * 1024 * 1024, "avb-erofs", true, true, "recovery_a"),
        ["recovery_b"] = new(1648L * 1024 * 1024, "avb-erofs", true, true, "recovery_b"),
        ["vbmeta_a"] = new(16L * 1024 * 1024, "avb-vbmeta", true, true, "vbmeta_a"),
        ["vbmeta_b"] = new(16L * 1024 * 1024, "avb-vbmeta", true, true, "vbmeta_b"),
        ["super"] = new(5776L * 1024 * 1024, "android-super", true, false),
    };

    public string StateDir { get; } = Env.String("HOMEHARBOR_RECOVERY_STATE_DIR", "/var/lib/homeharbor/recovery");
    public string DownloadPath { get; }
    public long MaxDownloadBytes { get; } = Env.Long("HOMEHARBOR_FASTBOOTD_MAX_DOWNLOAD_BYTES", 4L * 1024 * 1024 * 1024);

    private string SuperDevice { get; } = Env.String("HOMEHARBOR_FASTBOOTD_SUPER_DEVICE", "/dev/disk/by-partlabel/super");
    private string EspPath { get; } = "/efi";
    private bool DryRun { get; } = Env.Bool("HOMEHARBOR_FASTBOOTD_DRY_RUN");
    private readonly HomeHarbor.Tooling.ICommandRunner _runner;
    private readonly HomeHarbor.Tooling.SuperMapper _superMapper;
    private readonly FastbootUnlockGate _unlockGate;
    private FastbootSessionAuthorization? _sessionAuthorization;

    public FastbootActions(FastbootUnlockGate? unlockGate = null)
    {
        _unlockGate = unlockGate ?? FastbootUnlockGate.FromEnvironment();
        DownloadPath = Path.Combine(StateDir, "download-" + Guid.NewGuid().ToString("N") + ".img");
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

        var unlocked = DestructiveActionsAllowed(out _);
        return name switch
        {
            "all" => $"product:HomeHarbor\nversion:0.4\nis-userspace:yes\nslot-count:2\nsecure:{(unlocked ? "no" : "yes")}\nunlocked:{(unlocked ? "yes" : "no")}",
            "product" => "HomeHarbor",
            "version" => "0.4",
            "is-userspace" => "yes",
            "slot-count" => "2",
            "current-slot" => ReadBootEnvValue("HOMEHARBOR_BOOT_SLOT").ToLowerInvariant(),
            "secure" => unlocked ? "no" : "yes",
            "unlocked" => unlocked ? "yes" : "no",
            "max-download-size" => "0x" + MaxDownloadBytes.ToString("x", CultureInfo.InvariantCulture),
            _ => "",
        };
    }

    public FastbootStatus AuthenticateSession(ReadOnlySpan<byte> authorizationToken)
    {
        if (_sessionAuthorization is not null)
        {
            return FastbootStatus.Fail("session authorization failed");
        }

        if (!_unlockGate.TryAuthorizeSession(authorizationToken, out var authorization) || authorization is null)
        {
            return FastbootStatus.Fail("session authorization failed");
        }

        _sessionAuthorization = authorization;
        return FastbootStatus.Okay("session authorized");
    }

    public async Task<FastbootStatus> RunAuthorizedOperationAsync(
        Func<CancellationToken, Task<FastbootStatus>> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (!DestructiveActionsAllowed(out var failure))
        {
            return FastbootStatus.Fail(failure);
        }

        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var monitorCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var monitor = MonitorAuthorizationAsync(operationCancellation, monitorCancellation.Token);
        try
        {
            var result = await operation(operationCancellation.Token);
            if (!IsCurrentSessionAuthorized())
            {
                ClearSessionAuthorization();
                return FastbootStatus.Fail("fastboot authorization expired or was revoked");
            }

            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !IsCurrentSessionAuthorized())
        {
            ClearSessionAuthorization();
            return FastbootStatus.Fail("fastboot authorization expired or was revoked");
        }
        finally
        {
            monitorCancellation.Cancel();
            try
            {
                await monitor;
            }
            catch (OperationCanceledException) when (monitorCancellation.IsCancellationRequested)
            {
            }
        }
    }

    public async Task<FastbootStatus> FlashAsync(string partition, CancellationToken cancellationToken)
    {
        if (!DestructiveActionsAllowed(out var lockedFailure))
        {
            return FastbootStatus.Fail(lockedFailure);
        }

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
        if (!DestructiveActionsAllowed(out var lockedFailure))
        {
            return FastbootStatus.Fail(lockedFailure);
        }

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
        if (!DestructiveActionsAllowed(out var lockedFailure))
        {
            return FastbootStatus.Fail(lockedFailure);
        }

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
        if (!DestructiveActionsAllowed(out var failure))
        {
            throw new InvalidOperationException(failure);
        }

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

    public bool DestructiveActionsAllowed(out string failure)
    {
        if (IsCurrentSessionAuthorized())
        {
            failure = string.Empty;
            return true;
        }

        ClearSessionAuthorization();
        failure = _unlockGate.IsUnlocked(out _)
            ? "fastboot session is not authorized; run oem auth with the token from the physical recovery console"
            : "fastboot is locked; unlock it from the physical recovery console and authenticate this TCP session";
        return false;
    }

    public void CleanupSession()
    {
        try
        {
            if (File.Exists(DownloadPath))
            {
                File.Delete(DownloadPath);
            }
        }
        finally
        {
            ClearSessionAuthorization();
        }
    }

    private void ClearSessionAuthorization()
    {
        var authorization = Interlocked.Exchange(ref _sessionAuthorization, null);
        if (authorization is null)
        {
            return;
        }

        try
        {
            _unlockGate.EndSession(authorization);
        }
        finally
        {
            authorization.Dispose();
        }
    }

    private bool IsCurrentSessionAuthorized()
    {
        var authorization = Volatile.Read(ref _sessionAuthorization);
        return authorization is not null && _unlockGate.IsSessionAuthorized(authorization, out _);
    }

    private async Task MonitorAuthorizationAsync(
        CancellationTokenSource operationCancellation,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
            if (!IsCurrentSessionAuthorized())
            {
                await operationCancellation.CancelAsync();
                return;
            }
        }
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
        foreach (var path in new[] { "/run/homeharbor-boot/boot.env", "/run/homeharbor/boot.env", "/var/lib/homeharbor/ota/current.env" })
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
