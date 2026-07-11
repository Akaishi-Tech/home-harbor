using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using HomeHarbor.Tooling;

namespace HomeHarbor.FullE2E.Tests.Infrastructure;

internal sealed partial class LibvirtHomeHarborVm : IAsyncDisposable
{
    private const string UefiBootWithoutSecureBoot = "uefi,firmware.feature0.name=secure-boot,firmware.feature0.enabled=no";
    private const string UefiInstallerBootWithoutSecureBoot = UefiBootWithoutSecureBoot + ",bootmenu.enable=off";

    private readonly ProcessRunner _processes;
    private readonly string _connect;
    private readonly bool _keepVm;
    private readonly bool _deleteDiskOnDispose;
    private readonly Uri _apiBaseUri = new("https://homeharbor.local");
    private X509Certificate2? _rootCa;

    private LibvirtHomeHarborVm(
        ProcessRunner processes,
        string connect,
        bool keepVm,
        string domain,
        string overlay,
        string image,
        string version,
        string mode,
        bool deleteDiskOnDispose)
    {
        _processes = processes;
        _connect = connect;
        _keepVm = keepVm;
        _deleteDiskOnDispose = deleteDiskOnDispose;
        Domain = domain;
        Overlay = overlay;
        Image = image;
        Version = version;
        Mode = mode;
        ReportPath = Path.Combine(RepoPaths.Work, $"{domain}-e2e-report.json");
    }

    public string Domain { get; }
    public string Overlay { get; }
    public string Image { get; }
    public string Version { get; }
    public string Mode { get; }
    public string ReportPath { get; }
    public string IpAddress { get; private set; } = string.Empty;
    public string SetupBootstrapCode { get; private set; } = string.Empty;
    public string RootCaFingerprint { get; private set; } = string.Empty;
    public Uri ApiBaseUri => _apiBaseUri;
    public Uri ProxyBaseUri => ApiBaseUri;

    public static async Task<string> InstallFromIsoAsync(
        ProcessRunner processes,
        E2EOptions options,
        CancellationToken cancellationToken)
    {
        _ = Directory.CreateDirectory(RepoPaths.Work);
        var domain = NewDomainName("installer");
        var disk = Path.Combine(RepoPaths.Work, $"{domain}-installed.qcow2");

        _ = await processes.RunRequiredAsync(
            "qemu-img",
            ["create", "-f", "qcow2", disk, options.DiskSize],
            RepoPaths.Root,
            timeout: TimeSpan.FromMinutes(2),
            cancellationToken: cancellationToken);

        var vm = new LibvirtHomeHarborVm(
            processes,
            options.Connect,
            options.KeepVm,
            domain,
            disk,
            options.IsoPath,
            options.Version,
            "installer",
            deleteDiskOnDispose: false);

        try
        {
            await vm.EnsureDomainDoesNotExistAsync(cancellationToken);
            await vm.StartInstallerAsync(cancellationToken);
            await vm.RunIsoInstallerAsync(options, cancellationToken);
            await vm.DestroyDomainAsync(cancellationToken);
            return disk;
        }
        catch
        {
            if (!options.KeepVm)
            {
                await vm.DestroyDomainAsync(CancellationToken.None);
                TryDelete(disk);
            }

            throw;
        }
    }

    public static async Task<LibvirtHomeHarborVm> BootNormalAsync(
        ProcessRunner processes,
        E2EOptions options,
        string image,
        bool deleteDiskOnDispose,
        CancellationToken cancellationToken)
    {
        var vm = await CreateInstalledAsync(processes, options, image, "normal", deleteDiskOnDispose, cancellationToken);
        try
        {
            await vm.StartInstalledAsync(cancellationToken);
            await vm.WaitForSerialAsync("passphrase|Passphrase|Password", TimeSpan.FromMinutes(4), cancellationToken);
            await vm.SendSerialLineAsync(options.DataPassphrase, cancellationToken);
            await vm.WaitForSerialAsync("login:", TimeSpan.FromMinutes(4), cancellationToken);
            await vm.WaitForNetworkAsync(cancellationToken);
            await vm.WaitForQemuAgentAsync(TimeSpan.FromMinutes(2), cancellationToken);
            vm.SetupBootstrapCode = await vm.WaitForSetupBootstrapCodeAsync(cancellationToken);
            await vm.WaitForCaddyCertificateAsync(cancellationToken);
            var health = await vm.WaitForJsonAsync("/api/system/health", TimeSpan.FromMinutes(3), cancellationToken);
            await vm.WaitForProxyAsync(cancellationToken);
            await vm.WriteReportAsync(health, cancellationToken);
            return vm;
        }
        catch
        {
            await vm.DisposeAsync();
            throw;
        }
    }

    public static async Task<LibvirtHomeHarborVm> BootRecoveryAsync(
        ProcessRunner processes,
        E2EOptions options,
        string image,
        bool deleteDiskOnDispose,
        CancellationToken cancellationToken)
    {
        var vm = await CreateInstalledAsync(processes, options, image, "recovery", deleteDiskOnDispose, cancellationToken);
        try
        {
            await vm.StartInstalledAsync(cancellationToken);
            await vm.WaitForSerialAsync("passphrase|Passphrase|Password", TimeSpan.FromMinutes(4), cancellationToken);
            await vm.SendSerialLineAsync(options.DataPassphrase, cancellationToken);
            await vm.WaitForSerialAsync("login:", TimeSpan.FromMinutes(4), cancellationToken);
            await vm.RequestRecoveryViaEfivarAsync(cancellationToken);
            await vm.WaitForSerialAsync("HomeHarbor recovery|fastboot:", TimeSpan.FromMinutes(4), cancellationToken);
            await vm.WaitForNetworkAsync(cancellationToken);
            await vm.WaitForFastbootTcpAsync(cancellationToken);
            await vm.WriteReportAsync(null, cancellationToken);
            return vm;
        }
        catch
        {
            await vm.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _rootCa?.Dispose();
        _rootCa = null;
        if (_keepVm)
        {
            Console.WriteLine($"Leaving VM and overlay for inspection: {Domain} {Overlay}");
            return;
        }

        await DestroyDomainAsync(CancellationToken.None);

        if (_deleteDiskOnDispose)
        {
            TryDelete(Overlay);
        }
    }

    private async Task DestroyDomainAsync(CancellationToken cancellationToken)
    {
        var exists = await ProcessRunner.RunAsync(
            "virsh",
            ["-c", _connect, "dominfo", Domain],
            RepoPaths.Root,
            timeout: TimeSpan.FromSeconds(20),
            cancellationToken: cancellationToken);
        if (exists.ExitCode == 0)
        {
            _ = await ProcessRunner.RunAsync(
                "virsh",
                ["-c", _connect, "destroy", Domain],
                RepoPaths.Root,
                timeout: TimeSpan.FromSeconds(30),
                cancellationToken: cancellationToken);
            var undefine = await ProcessRunner.RunAsync(
                "virsh",
                ["-c", _connect, "undefine", Domain, "--nvram"],
                RepoPaths.Root,
                timeout: TimeSpan.FromSeconds(30),
                cancellationToken: cancellationToken);
            if (undefine.ExitCode != 0)
            {
                _ = await ProcessRunner.RunAsync(
                    "virsh",
                    ["-c", _connect, "undefine", Domain],
                    RepoPaths.Root,
                    timeout: TimeSpan.FromSeconds(30),
                    cancellationToken: cancellationToken);
            }
        }
    }

    private static async Task<LibvirtHomeHarborVm> CreateInstalledAsync(
        ProcessRunner processes,
        E2EOptions options,
        string disk,
        string mode,
        bool deleteDiskOnDispose,
        CancellationToken cancellationToken)
    {
        _ = Directory.CreateDirectory(RepoPaths.Work);
        var domain = NewDomainName(mode);
        var vm = new LibvirtHomeHarborVm(
            processes,
            options.Connect,
            options.KeepVm,
            domain,
            disk,
            disk,
            options.Version,
            mode,
            deleteDiskOnDispose);

        _ = await processes.RunRequiredAsync(
            "virsh",
            ["-c", options.Connect, "list", "--all"],
            RepoPaths.Root,
            timeout: TimeSpan.FromSeconds(20),
            cancellationToken: cancellationToken);

        await vm.EnsureDomainDoesNotExistAsync(cancellationToken);

        return vm;
    }

    private async Task EnsureDomainDoesNotExistAsync(CancellationToken cancellationToken)
    {
        var existing = await ProcessRunner.RunAsync(
            "virsh",
            ["-c", _connect, "dominfo", Domain],
            RepoPaths.Root,
            timeout: TimeSpan.FromSeconds(20),
            cancellationToken: cancellationToken);
        if (existing.ExitCode == 0)
        {
            throw new AssertFailedException($"Domain already exists: {Domain}");
        }
    }

    private async Task StartInstallerAsync(CancellationToken cancellationToken)
    {
        _ = await _processes.RunRequiredAsync(
            "virt-install",
            [
                "--connect", _connect,
                "--name", Domain,
                "--memory", "4096",
                "--vcpus", "2",
                "--cpu", "host-passthrough",
                "--disk", $"path={Path.GetFullPath(Overlay)},format=qcow2,bus=virtio",
                "--disk", $"path={Path.GetFullPath(Image)},device=cdrom,readonly=on",
                "--os-variant", "detect=on,require=off",
                "--network", "network=default,model=virtio",
                "--boot", UefiInstallerBootWithoutSecureBoot,
                "--graphics", "none",
                "--serial", "pty",
                "--console", "pty,target_type=serial",
                "--noautoconsole"
            ],
            RepoPaths.Root,
            timeout: TimeSpan.FromMinutes(3),
            cancellationToken: cancellationToken);
    }

    private async Task RunIsoInstallerAsync(E2EOptions options, CancellationToken cancellationToken)
    {
        _ = options;
        await WaitForSerialAsync("HomeHarbor Live Installer", TimeSpan.FromMinutes(8), cancellationToken);
        Assert.Inconclusive("The ISO installer is TUI-only; FullE2E ISO automation must drive the graphical TUI with screenshots and virsh send-key.");
    }

    private async Task StartInstalledAsync(CancellationToken cancellationToken)
    {
        _ = await _processes.RunRequiredAsync(
            "virt-install",
            [
                "--connect", _connect,
                "--name", Domain,
                "--memory", "4096",
                "--vcpus", "2",
                "--cpu", "host-passthrough",
                "--import",
                "--disk", $"path={Path.GetFullPath(Overlay)},format=qcow2,bus=virtio",
                "--os-variant", "detect=on,require=off",
                "--network", "network=default,model=virtio",
                "--boot", UefiBootWithoutSecureBoot,
                "--graphics", "none",
                "--channel", "unix,target.type=virtio,target.name=org.qemu.guest_agent.0",
                "--serial", "pty",
                "--console", "pty,target_type=serial",
                "--noautoconsole"
            ],
            RepoPaths.Root,
            timeout: TimeSpan.FromMinutes(3),
            cancellationToken: cancellationToken);
    }

    private async Task WaitForSerialAsync(string expect, TimeSpan timeout, CancellationToken cancellationToken)
    {
        _ = await _processes.RunRequiredAsync(
            "python3",
            [
                Path.Combine(RepoPaths.Root, "tests", "HomeHarbor.FullE2E.Tests", "Tools", "virsh-console-io.py"),
                Domain,
                "--connect", _connect,
                "--expect", expect,
                "--read-seconds", Math.Ceiling(timeout.TotalSeconds).ToString("0", CultureInfo.InvariantCulture)
            ],
            RepoPaths.Root,
            timeout: timeout + TimeSpan.FromSeconds(15),
            cancellationToken: cancellationToken);
    }

    private async Task SendSerialLineAsync(string line, CancellationToken cancellationToken)
    {
        _ = await _processes.RunRequiredAsync(
            "python3",
            [
                Path.Combine(RepoPaths.Root, "tests", "HomeHarbor.FullE2E.Tests", "Tools", "virsh-console-io.py"),
                Domain,
                "--connect", _connect,
                "--send-line", line,
                "--read-seconds", "0",
                "--quiet"
            ],
            RepoPaths.Root,
            timeout: TimeSpan.FromSeconds(20),
            cancellationToken: cancellationToken);
    }

    private async Task RequestRecoveryViaEfivarAsync(CancellationToken cancellationToken)
    {
        await WaitForQemuAgentAsync(TimeSpan.FromMinutes(2), cancellationToken);
        await RunGuestShellAsync(
            string.Join('\n', new[]
            {
                "set -eu",
                "payload=/run/homeharbor-boot-next",
                "printf '%s' 'recovery' > \"${payload}\"",
                "efivar -w -A 7 -n '" + EfiBootVariables.FullBootNextName + "' -f \"${payload}\""
            }),
            TimeSpan.FromSeconds(30),
            cancellationToken);

        var reboot = await ProcessRunner.RunAsync(
            "virsh",
            ["-c", _connect, "qemu-agent-command", Domain, "{\"execute\":\"guest-shutdown\",\"arguments\":{\"mode\":\"reboot\"}}"],
            RepoPaths.Root,
            timeout: TimeSpan.FromSeconds(20),
            cancellationToken: cancellationToken);
        if (reboot.ExitCode != 0)
        {
            Console.WriteLine(
                "QEMU guest-shutdown returned non-zero while requesting reboot; " +
                "continuing to serial recovery wait because the guest agent may disappear during reboot.\n" +
                "STDOUT:\n" + reboot.Stdout + "\nSTDERR:\n" + reboot.Stderr);
        }
    }

    private async Task WaitForQemuAgentAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        ProcessResult? last = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            last = await ProcessRunner.RunAsync(
                "virsh",
                ["-c", _connect, "qemu-agent-command", Domain, "{\"execute\":\"guest-ping\"}"],
                RepoPaths.Root,
                timeout: TimeSpan.FromSeconds(10),
                cancellationToken: cancellationToken);
            if (last.ExitCode == 0)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }

        throw new AssertFailedException(
            $"Timed out waiting for QEMU guest agent in {Domain}.\nSTDOUT:\n{last?.Stdout}\nSTDERR:\n{last?.Stderr}");
    }

    private async Task RunGuestShellAsync(string script, TimeSpan timeout, CancellationToken cancellationToken)
    {
        _ = await CaptureGuestShellAsync(script, timeout, cancellationToken);
    }

    private async Task<string> CaptureGuestShellAsync(string script, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var start = await RunQemuAgentCommandAsync(
            new JsonObject
            {
                ["execute"] = "guest-exec",
                ["arguments"] = new JsonObject
                {
                    ["path"] = "/bin/sh",
                    ["arg"] = new JsonArray { "-c", script },
                    ["capture-output"] = true
                }
            },
            TimeSpan.FromSeconds(10),
            cancellationToken);
        start.EnsureSuccess();

        var pid = JsonNode.Parse(start.Stdout)?["return"]?["pid"]?.GetValue<int>()
            ?? throw new AssertFailedException("QEMU guest agent did not return a guest-exec pid: " + start.Stdout);
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        JsonObject? lastReturn = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var status = await RunQemuAgentCommandAsync(
                new JsonObject
                {
                    ["execute"] = "guest-exec-status",
                    ["arguments"] = new JsonObject { ["pid"] = pid }
                },
                TimeSpan.FromSeconds(10),
                cancellationToken);
            status.EnsureSuccess();
            lastReturn = JsonNode.Parse(status.Stdout)?["return"]?.AsObject();
            if (lastReturn?["exited"]?.GetValue<bool>() == true)
            {
                var exitCode = lastReturn["exitcode"]?.GetValue<int>() ?? 0;
                if (exitCode == 0)
                {
                    return DecodeGuestExecData(lastReturn["out-data"]);
                }

                throw new AssertFailedException(
                    $"Guest command failed with exit code {exitCode}.\nSTDOUT:\n{DecodeGuestExecData(lastReturn["out-data"])}\nSTDERR:\n{DecodeGuestExecData(lastReturn["err-data"])}");
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }

        throw new AssertFailedException(
            $"Timed out waiting for guest command pid {pid}.\nSTDOUT:\n{DecodeGuestExecData(lastReturn?["out-data"])}\nSTDERR:\n{DecodeGuestExecData(lastReturn?["err-data"])}");
    }

    private async Task<ProcessResult> RunQemuAgentCommandAsync(JsonObject command, TimeSpan timeout, CancellationToken cancellationToken)
        => await ProcessRunner.RunAsync(
            "virsh",
            ["-c", _connect, "qemu-agent-command", Domain, command.ToJsonString()],
            RepoPaths.Root,
            timeout: timeout,
            cancellationToken: cancellationToken);

    private static string DecodeGuestExecData(JsonNode? node)
    {
        var value = node?.GetValue<string>();
        return string.IsNullOrEmpty(value) ? string.Empty : Encoding.UTF8.GetString(Convert.FromBase64String(value));
    }

    private async Task WaitForNetworkAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(3);
        var mac = await TryReadMacAddressAsync(cancellationToken);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var ip = await TryReadIpAddressAsync(mac, cancellationToken);
            if (!string.IsNullOrWhiteSpace(ip))
            {
                IpAddress = ip;
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }

        throw new AssertFailedException($"VM {Domain} did not receive a DHCP lease.");
    }

    private async Task<string?> TryReadMacAddressAsync(CancellationToken cancellationToken)
    {
        var result = await ProcessRunner.RunAsync(
            "virsh",
            ["-c", _connect, "domiflist", Domain],
            RepoPaths.Root,
            timeout: TimeSpan.FromSeconds(10),
            cancellationToken: cancellationToken);
        if (result.ExitCode != 0) return null;

        foreach (var line in result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 5 && string.Equals(parts[1], "network", StringComparison.OrdinalIgnoreCase))
            {
                return parts[4];
            }
        }

        return null;
    }

    private async Task<string?> TryReadIpAddressAsync(string? mac, CancellationToken cancellationToken)
    {
        var domifaddr = await ProcessRunner.RunAsync(
            "virsh",
            ["-c", _connect, "domifaddr", Domain, "--source", "lease"],
            RepoPaths.Root,
            timeout: TimeSpan.FromSeconds(10),
            cancellationToken: cancellationToken);
        if (domifaddr.ExitCode == 0)
        {
            var ip = ParseFirstIpv4(domifaddr.Stdout);
            if (!string.IsNullOrWhiteSpace(ip)) return ip;
        }

        if (string.IsNullOrWhiteSpace(mac)) return null;
        var leases = await ProcessRunner.RunAsync(
            "virsh",
            ["-c", _connect, "net-dhcp-leases", "default"],
            RepoPaths.Root,
            timeout: TimeSpan.FromSeconds(10),
            cancellationToken: cancellationToken);
        if (leases.ExitCode != 0) return null;

        foreach (var line in leases.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!line.Contains(mac, StringComparison.OrdinalIgnoreCase)) continue;
            var ip = ParseFirstIpv4(line);
            if (!string.IsNullOrWhiteSpace(ip)) return ip;
        }

        return null;
    }

    public HttpClient CreateTrustedHttpClient()
    {
        if (_rootCa is null || string.IsNullOrWhiteSpace(IpAddress))
        {
            throw new InvalidOperationException("VM HTTPS trust has not been established");
        }

        var trustedRoot = _rootCa;
        var targetAddress = IPAddress.Parse(IpAddress);
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            ConnectCallback = async (context, cancellationToken) =>
            {
                var socket = new Socket(targetAddress.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(
                        new IPEndPoint(targetAddress, context.DnsEndPoint.Port),
                        cancellationToken);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
        };
        handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
        {
            if (certificate is null ||
                (errors & (SslPolicyErrors.RemoteCertificateNotAvailable | SslPolicyErrors.RemoteCertificateNameMismatch)) != 0)
            {
                return false;
            }

            using var leaf = X509CertificateLoader.LoadCertificate(certificate.Export(X509ContentType.Cert));
            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(trustedRoot);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
            return chain.Build(leaf);
        };

        return new HttpClient(handler)
        {
            BaseAddress = ApiBaseUri,
            Timeout = TimeSpan.FromSeconds(20)
        };
    }

    private async Task<string> WaitForSetupBootstrapCodeAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(2);
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                var code = (await CaptureGuestShellAsync(
                    "cat /var/lib/homeharbor/setup/bootstrap-code",
                    TimeSpan.FromSeconds(10),
                    cancellationToken)).Trim();
                if (Regex.IsMatch(code, "^[A-HJ-NP-Z2-9]{4}(?:-[A-HJ-NP-Z2-9]{4}){3}$", RegexOptions.CultureInvariant))
                {
                    return code;
                }
            }
            catch (AssertFailedException)
            {
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }

        throw new AssertFailedException("Timed out waiting for the physical setup bootstrap code inside the disposable VM");
    }

    private async Task WaitForCaddyCertificateAsync(CancellationToken cancellationToken)
    {
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            BaseAddress = new Uri($"http://{IpAddress}"),
            Timeout = TimeSpan.FromSeconds(5)
        };
        var deadline = DateTimeOffset.UtcNow.AddMinutes(2);
        Exception? last = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                using var response = await client.GetAsync(CaddyTrustConfiguration.CertificateDownloadPath, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                    if (bytes.Length is > 0 and <= 1024 * 1024)
                    {
                        var certificate = X509Certificate2.CreateFromPem(Encoding.UTF8.GetString(bytes));
                        var constraints = certificate.Extensions.OfType<X509BasicConstraintsExtension>().FirstOrDefault();
                        if (constraints?.CertificateAuthority == true)
                        {
                            _rootCa?.Dispose();
                            _rootCa = certificate;
                            RootCaFingerprint = string.Join(':', SHA256.HashData(certificate.RawData)
                                .Select(value => value.ToString("X2", CultureInfo.InvariantCulture)));
                            return;
                        }

                        certificate.Dispose();
                    }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or CryptographicException)
            {
                last = ex;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }

        throw new AssertFailedException($"Timed out waiting for HomeHarbor public CA certificate: {last}");
    }

    private async Task<JsonObject> WaitForJsonAsync(string path, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var client = CreateTrustedHttpClient();
        client.Timeout = TimeSpan.FromSeconds(5);
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        Exception? last = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                using var response = await client.GetAsync(path, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync(cancellationToken);
                    return JsonNode.Parse(body)?.AsObject()
                        ?? throw new JsonException($"Response is not a JSON object: {body}");
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                last = ex;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }

        throw new AssertFailedException($"Timed out waiting for {ApiBaseUri}{path}: {last}");
    }

    private async Task WaitForProxyAsync(CancellationToken cancellationToken)
    {
        using var client = CreateTrustedHttpClient();
        client.Timeout = TimeSpan.FromSeconds(5);
        var deadline = DateTimeOffset.UtcNow.AddMinutes(2);
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                using var response = await client.GetAsync("/", cancellationToken);
                if (response.IsSuccessStatusCode) return;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }

        throw new AssertFailedException($"Timed out waiting for Caddy reverse proxy at {ProxyBaseUri}.");
    }

    private async Task WaitForFastbootTcpAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(2);
        ProcessResult? last = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            last = await ProcessRunner.RunAsync(
                "fastboot",
                ["-s", $"tcp:{IpAddress}:5554", "getvar", "product"],
                RepoPaths.Root,
                timeout: TimeSpan.FromSeconds(10),
                cancellationToken: cancellationToken);
            if (last.ExitCode == 0 && (last.Stdout + last.Stderr).Contains("HomeHarbor", StringComparison.Ordinal))
            {
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }

        throw new AssertFailedException(
            $"Timed out waiting for recovery fastboot TCP on {IpAddress}:5554.\nSTDOUT:\n{last?.Stdout}\nSTDERR:\n{last?.Stderr}");
    }

    private async Task WriteReportAsync(JsonObject? health, CancellationToken cancellationToken)
    {
        var imageSha = await Sha256FileAsync(Image, cancellationToken);
        var report = new JsonObject
        {
            ["checks"] = new JsonObject
            {
                ["apiHealth"] = health is null ? "not-applicable" : "passed",
                ["dhcpLease"] = "passed",
                ["proxy"] = string.Equals(Mode, "normal", StringComparison.Ordinal) ? "passed" : "not-applicable",
                ["tlsCaTrust"] = string.Equals(Mode, "normal", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(RootCaFingerprint)
                    ? "passed"
                    : "not-applicable",
                ["serialLogin"] = "passed",
                ["fastbootTcp"] = string.Equals(Mode, "recovery", StringComparison.Ordinal) ? "passed" : "not-applicable"
            },
            ["connect"] = _connect,
            ["createdAt"] = DateTimeOffset.UtcNow.ToString("O"),
            ["domain"] = Domain,
            ["image"] = Path.GetFullPath(Image),
            ["imageFile"] = Path.GetFileName(Image),
            ["imageFormat"] = DetectImageFormat(Image),
            ["imageSha256"] = imageSha,
            ["imageSizeBytes"] = new FileInfo(Image).Length,
            ["ipAddress"] = IpAddress,
            ["mode"] = Mode,
            ["rootCaSha256"] = string.IsNullOrWhiteSpace(RootCaFingerprint) ? null : RootCaFingerprint,
            ["result"] = "passed",
            ["version"] = Version
        };

        if (health is not null)
        {
            var healthText = health.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
            report["healthEndpoint"] = new Uri(ApiBaseUri, "api/system/health").ToString();
            report["healthResponse"] = health.DeepClone();
            report["healthSha256"] = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(healthText))).ToLowerInvariant();
            report["proxyUrl"] = ProxyBaseUri.ToString();
        }

        await File.WriteAllTextAsync(
            ReportPath,
            report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
            cancellationToken);

        var parsed = JsonNode.Parse(await File.ReadAllTextAsync(ReportPath, cancellationToken))?.AsObject();
        Assert.AreEqual("passed", parsed?["result"]?.GetValue<string>());
        Assert.AreEqual(imageSha, parsed?["imageSha256"]?.GetValue<string>());
    }

    private static string DetectImageFormat(string image)
        => image.EndsWith(".qcow2", StringComparison.OrdinalIgnoreCase)
            ? "qcow2"
            : image.EndsWith(".raw", StringComparison.OrdinalIgnoreCase) || image.EndsWith(".img", StringComparison.OrdinalIgnoreCase)
                ? "raw"
                : "qcow2";

    private static string NewDomainName(string mode)
        => $"homeharbor-e2e-{mode}-{Environment.ProcessId}-{Guid.NewGuid():N}"[..44];

    private static async Task<string> Sha256FileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string? ParseFirstIpv4(string text)
    {
        var match = Ipv4WithOptionalCidrRegex().Match(text);
        return match.Success ? match.Groups["ip"].Value : null;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [GeneratedRegex(@"(?<ip>(?:\d{1,3}\.){3}\d{1,3})(?:/\d{1,2})?")]
    private static partial Regex Ipv4WithOptionalCidrRegex();
}
