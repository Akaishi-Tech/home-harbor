using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using HomeHarbor.Tooling;
using Terminal.Gui.Drivers;

namespace HomeHarbor.Tests;

[TestClass]
public sealed class InstallerTuiTests
{
    [TestMethod]
    public void TerminalGuiInstallerUi_ResolveDriverName_Defaults_To_Dotnet()
    {
        var driver = global::TerminalGuiInstallerUi.ResolveDriverName(null);

        Assert.AreEqual(DriverRegistry.Names.DOTNET, driver);
    }

    [TestMethod]
    public void TerminalGuiInstallerUi_ResolveDriverName_Rejects_Ansi()
    {
        var ex = Assert.ThrowsExactly<ArgumentException>(() => global::TerminalGuiInstallerUi.ResolveDriverName("ansi"));

        Assert.Contains("ANSI Terminal.Gui driver is not supported", ex.Message);
    }

    [TestMethod]
    public void TerminalGuiInstallerUi_ApplySizeDetectionMode_Defaults_To_Polling()
    {
        var original = Driver.SizeDetection;
        try
        {
            Driver.SizeDetection = SizeDetectionMode.AnsiQuery;

            global::TerminalGuiInstallerUi.ApplySizeDetectionMode(null);

            Assert.AreEqual(SizeDetectionMode.Polling, Driver.SizeDetection);
        }
        finally
        {
            Driver.SizeDetection = original;
        }
    }

    [TestMethod]
    public void TerminalGuiInstallerUi_ApplySizeDetectionMode_Rejects_AnsiQuery()
    {
        var ex = Assert.ThrowsExactly<ArgumentException>(() => global::TerminalGuiInstallerUi.ApplySizeDetectionMode("ansi-query"));

        Assert.Contains("ANSI terminal size detection is not supported", ex.Message);
    }

    [TestMethod]
    public void TerminalGuiInstallerUi_CreateSelectList_Defaults_To_First_Item()
    {
        var list = global::TerminalGuiInstallerUi.CreateSelectList(["Install HomeHarbor", "Diagnostics"]);

        Assert.AreEqual(0, list.SelectedItem);
    }

    [TestMethod]
    public async Task ProcessRunner_CaptureAsync_Reads_Stdout_And_Stderr_Concurrently()
    {
        const string script = """
            i=0
            while [ "$i" -lt 20000 ]; do
              printf 'stderr line %05d\n' "$i" >&2
              i=$((i + 1))
            done
            printf 'stdout-ok\n'
            """;

        var capture = global::ProcessRunner.CaptureAsync("/bin/sh", ["-c", script]);
        var completed = await Task.WhenAny(capture, Task.Delay(TimeSpan.FromSeconds(5)));

        Assert.AreSame(capture, completed);
        var (ExitCode, Output) = await capture;
        Assert.AreEqual(0, ExitCode);
        Assert.Contains("stdout-ok", Output);
        Assert.Contains("stderr line 19999", Output);
    }

    [TestMethod]
    public void DiskInfo_ParseListJson_Includes_Disk_Metadata_And_Mount_Warnings()
    {
        const string json = """
            {
              "blockdevices": [
                {
                  "path": "/dev/vda",
                  "size": 68719476736,
                  "model": "VirtIO Block Device",
                  "type": "disk",
                  "serial": "disk-a",
                  "tran": "virtio",
                  "mountpoints": [null, "/mnt/data"]
                },
                {
                  "path": "/dev/vda1",
                  "size": 1048576,
                  "model": "partition",
                  "type": "part",
                  "mountpoints": ["/boot"]
                },
                {
                  "path": "/dev/sdb",
                  "size": "34359738368",
                  "type": "disk",
                  "mountpoints": null
                }
              ]
            }
            """;

        var disks = global::DiskInfo.ParseListJson(json);

        Assert.HasCount(2, disks);
        Assert.AreEqual("/dev/vda", disks[0].Path);
        Assert.AreEqual(68719476736, disks[0].SizeBytes);
        Assert.AreEqual("VirtIO Block Device", disks[0].Model);
        Assert.AreEqual("disk-a", disks[0].Serial);
        Assert.AreEqual("virtio", disks[0].Transport);
        CollectionAssert.AreEqual(new[] { "/mnt/data" }, disks[0].Mountpoints.ToArray());
        Assert.IsTrue(disks[0].HasMounts);
        Assert.AreEqual("disk", disks[1].Model);
        Assert.AreEqual(34359738368, disks[1].SizeBytes);
        Assert.IsFalse(disks[1].HasMounts);
    }

    [TestMethod]
    public void NetworkManagerClient_ParseWifiAccessPoints_Handles_Terse_Escapes_And_Hidden_Ssids()
    {
        const string output = """
            Cafe\:Lab:AA\:BB\:CC\:DD\:EE\:FF:88:WPA2
            :11\:22\:33\:44\:55\:66:44:WPA1 WPA2
            OpenNet:77\:88\:99\:AA\:BB\:CC:101:
            """;

        var accessPoints = global::NetworkManagerClient.ParseWifiAccessPoints(output);

        Assert.HasCount(3, accessPoints);
        Assert.AreEqual("Cafe:Lab", accessPoints[0].Ssid);
        Assert.AreEqual("AA:BB:CC:DD:EE:FF", accessPoints[0].Bssid);
        Assert.AreEqual(88, accessPoints[0].Signal);
        Assert.AreEqual("WPA2", accessPoints[0].Security);
        Assert.IsTrue(accessPoints[0].RequiresPassword);
        Assert.IsTrue(accessPoints[1].IsHidden);
        Assert.AreEqual(44, accessPoints[1].Signal);
        Assert.AreEqual(100, accessPoints[2].Signal);
        Assert.IsFalse(accessPoints[2].RequiresPassword);
    }

    [TestMethod]
    public void NetworkManagerClient_ParseDeviceStatus_Handles_Escaped_Connection_Names()
    {
        const string output = """
            enp1s0:ethernet:connected:Wired\:Lab
            wlan0:wifi:disconnected:--
            """;

        var devices = global::NetworkManagerClient.ParseDeviceStatus(output);

        Assert.HasCount(2, devices);
        Assert.AreEqual("enp1s0", devices[0].Name);
        Assert.AreEqual("Wired:Lab", devices[0].Connection);
        Assert.IsTrue(devices[0].IsEthernet);
        Assert.IsTrue(devices[0].IsConnected);
        Assert.IsTrue(devices[1].IsWifi);
        Assert.IsFalse(devices[1].IsConnected);
    }

    [TestMethod]
    public void InstallerIpConfiguration_Static_Validates_And_Normalizes_Dns()
    {
        var valid = global::InstallerIpConfiguration.TryCreateStatic(
            "192.168.1.20/24",
            "192.168.1.1",
            "1.1.1.1, 8.8.8.8",
            out var configuration,
            out var error);

        Assert.IsTrue(valid, error);
        Assert.IsNotNull(configuration);
        Assert.IsFalse(configuration.UseDhcp);
        Assert.AreEqual("192.168.1.20/24", configuration.AddressWithPrefix);
        Assert.AreEqual("192.168.1.1", configuration.Gateway);
        CollectionAssert.AreEqual(new[] { "1.1.1.1", "8.8.8.8" }, configuration.Dns.ToArray());
    }

    [TestMethod]
    [DataRow("192.168.1.20", "192.168.1.1", "1.1.1.1")]
    [DataRow("192.168.1.20/33", "192.168.1.1", "1.1.1.1")]
    [DataRow("not-ip/24", "192.168.1.1", "1.1.1.1")]
    [DataRow("192.168.1.20/24", "", "1.1.1.1")]
    [DataRow("192.168.1.20/24", "not-ip", "1.1.1.1")]
    [DataRow("192.168.1.20/24", "192.168.1.1", "not-ip")]
    public void InstallerIpConfiguration_Static_Rejects_Invalid_Input(string address, string gateway, string dns)
    {
        var valid = global::InstallerIpConfiguration.TryCreateStatic(
            address,
            gateway,
            dns,
            out var configuration,
            out var error);

        Assert.IsFalse(valid);
        Assert.IsNull(configuration);
        Assert.IsFalse(string.IsNullOrWhiteSpace(error));
    }

    [TestMethod]
    public async Task NetworkManagerClient_ConnectWired_Dhcp_Uses_Temporary_Profile()
    {
        var runner = new RecordingNetworkRunner();
        var client = new global::NetworkManagerClient(runner);
        var device = new global::NetworkDevice("enp1s0", "ethernet", "disconnected", "--");

        await client.ConnectWiredAsync(device, global::InstallerIpConfiguration.Dhcp);

        AssertCall(runner.Calls, "nmcli", ["connection", "delete", "id", "HomeHarbor installer wired enp1s0"]);
        AssertCall(runner.Calls, "nmcli", ["connection", "add", "type", "ethernet", "ifname", "enp1s0", "con-name", "HomeHarbor installer wired enp1s0"]);
        AssertCall(runner.Calls, "nmcli", ["connection", "modify", "id", "HomeHarbor installer wired enp1s0", "ipv4.method", "auto", "ipv6.method", "auto"]);
        AssertCall(runner.Calls, "nmcli", ["connection", "up", "id", "HomeHarbor installer wired enp1s0", "ifname", "enp1s0"]);
    }

    [TestMethod]
    public async Task NetworkManagerClient_ConnectWifi_Hidden_Static_Uses_Passwd_File_Without_Secret_Args()
    {
        var runner = new RecordingNetworkRunner();
        var client = new global::NetworkManagerClient(runner);
        var device = new global::NetworkDevice("wlan0", "wifi", "disconnected", "--");
        Assert.IsTrue(global::InstallerIpConfiguration.TryCreateStatic(
            "10.0.0.20/24",
            "10.0.0.1",
            "9.9.9.9",
            out var ipConfiguration,
            out var error), error);

        await client.ConnectWifiAsync(device, "Hidden Lab", hidden: true, "super-secret-wifi", ipConfiguration!);

        AssertCall(runner.Calls, "nmcli", ["connection", "add", "type", "wifi", "ifname", "wlan0", "con-name", "HomeHarbor installer wifi Hidden Lab", "ssid", "Hidden Lab"]);
        AssertCall(runner.Calls, "nmcli", ["connection", "modify", "id", "HomeHarbor installer wifi Hidden Lab", "802-11-wireless.hidden", "yes"]);
        AssertCall(runner.Calls, "nmcli", ["connection", "modify", "id", "HomeHarbor installer wifi Hidden Lab", "802-11-wireless-security.key-mgmt", "wpa-psk"]);
        AssertCall(runner.Calls, "nmcli", [
            "connection",
            "modify",
            "id",
            "HomeHarbor installer wifi Hidden Lab",
            "ipv4.method",
            "manual",
            "ipv4.addresses",
            "10.0.0.20/24",
            "ipv4.gateway",
            "10.0.0.1",
            "ipv4.dns",
            "9.9.9.9",
            "ipv6.method",
            "auto"
        ]);

        var upCall = runner.Calls.Single(call => call.Arguments.Contains("passwd-file", StringComparer.Ordinal));
        CollectionAssert.DoesNotContain(upCall.Arguments, "super-secret-wifi");
        Assert.Contains("802-11-wireless-security.psk:super-secret-wifi", upCall.Payload);
        Assert.IsFalse(string.Join(" ", runner.Calls.SelectMany(call => call.Arguments)).Contains("super-secret-wifi", StringComparison.Ordinal));
        Assert.IsFalse(new global::WifiAccessPoint("Hidden Lab", "", 80, "WPA2").Summary.Contains("super-secret-wifi", StringComparison.Ordinal));
    }

    [TestMethod]
    public void InstallerDryRunResult_Parses_And_Renders_Success_Report()
    {
        var result = global::InstallerDryRunResult.FromProcess(0, """
            HomeHarbor installer dry run.
            target=/dev/vda
            version=0.1.0
            channel=stable
            bootMode=raw-uki
            systemOta=/payload/homeharbor-system-ota-0.1.0.tar.gz
            kernelOta=
            diskSizeBytes=68719476736
            minimumBytes=34359738368
            installWorkMinMiB=24576
            dataSetup=web-oobe
            willEnrollMok=no
            willWritePartitions=esp,boot_a,boot_b,super,state,recovery_a,recovery_b,vbmeta_a,vbmeta_b,data-candidate
            """);

        var report = result.FormatForConfirmation();

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual("0.1.0", result.Values["version"]);
        Assert.AreEqual("web-oobe", result.Values["dataSetup"]);
        Assert.Contains("Preflight validation passed.", report);
        Assert.Contains("Version:             0.1.0", report);
        Assert.Contains("Minimum disk size:   32 GiB", report);
        Assert.Contains("Partitions to write: esp,boot_a,boot_b,super,state,recovery_a,recovery_b,vbmeta_a,vbmeta_b,data-candidate", report);
        Assert.IsFalse(report.Contains("Data setup", StringComparison.Ordinal));
        Assert.IsFalse(report.Contains("web-oobe", StringComparison.Ordinal));
    }

    [TestMethod]
    public void InstallerDryRunResult_Renders_Failure_Output()
    {
        var result = global::InstallerDryRunResult.FromProcess(1, "target is too small: 1 bytes, need at least 2");

        var report = result.FormatForConfirmation();

        Assert.IsFalse(result.Succeeded);
        Assert.Contains("Preflight validation failed with exit code 1.", report);
        Assert.Contains("target is too small", report);
    }

    [TestMethod]
    public void InstallDiskExecutor_ParseVerityDescriptorArg_Reads_Hashtree_Geometry()
    {
        var descriptor = global::InstallDiskExecutor.ParseVerityDescriptorArg(
            "sha256:4096:4096:98304:402653184:706a80c552baf758cec245a5b42cb5a0b1ba07bfc7c435162e3ee68fca6a82ae:616a66d1d49d72f2e80d13afdd38283e55464789d2c8ea182868bcdd00ccacb5",
            "modules",
            "modules_a.verity");

        Assert.AreEqual("modules", descriptor.PartitionName);
        Assert.AreEqual("sha256", descriptor.HashAlgorithm);
        Assert.AreEqual(4096, descriptor.DataBlockSize);
        Assert.AreEqual(4096, descriptor.HashBlockSize);
        Assert.AreEqual(98304, descriptor.DataBlocks);
        Assert.AreEqual(402653184, descriptor.ImageSizeBytes);
        Assert.AreEqual(402653184, descriptor.TreeOffset);
        Assert.AreEqual("706a80c552baf758cec245a5b42cb5a0b1ba07bfc7c435162e3ee68fca6a82ae", descriptor.Salt);
        Assert.AreEqual("616a66d1d49d72f2e80d13afdd38283e55464789d2c8ea182868bcdd00ccacb5", descriptor.RootDigest);
    }

    [TestMethod]
    public void InstallDiskExecutor_ParseAvbInfoImageDescriptor_Selects_Partition()
    {
        const string output = """
            Minimum libavb version:   1.1
            Descriptors:
                Hashtree descriptor:
                  Image Size:            4096 bytes
                  Tree Offset:           4096
                  Data Block Size:       4096 bytes
                  Hash Block Size:       4096 bytes
                  Hash Algorithm:        sha256
                  Partition Name:        other
                  Salt:                  0001
                  Root Digest:           0002
                Hashtree descriptor:
                  Image Size:            8192 bytes
                  Tree Offset:           8192
                  Data Block Size:       4096 bytes
                  Hash Block Size:       4096 bytes
                  Hash Algorithm:        sha256
                  Partition Name:        root
                  Salt:                  0a0b
                  Root Digest:           0c0d
            """;

        var descriptor = global::InstallDiskExecutor.ParseAvbInfoImageDescriptor(output, "root");

        Assert.AreEqual("root", descriptor.PartitionName);
        Assert.AreEqual(2, descriptor.DataBlocks);
        Assert.AreEqual(8192, descriptor.ImageSizeBytes);
        Assert.AreEqual("0a0b", descriptor.Salt);
        Assert.AreEqual("0c0d", descriptor.RootDigest);
    }

    [TestMethod]
    public void InstallDiskExecutor_AvbDescriptorPartitionName_Strips_Ab_Suffix()
    {
        Assert.AreEqual("root", HomeHarbor.Tooling.AvbPartitionNames.DescriptorName("root_a"));
        Assert.AreEqual("modules", HomeHarbor.Tooling.AvbPartitionNames.DescriptorName("modules_b"));
        Assert.AreEqual("vbmeta", HomeHarbor.Tooling.AvbPartitionNames.DescriptorName("vbmeta"));
    }

    [TestMethod]
    public void InstallerDiagnostics_Redacts_Secret_Environment_Values()
    {
        var variables = new[]
        {
            new KeyValuePair<string, string?>("HOMEHARBOR_DATA_PASSPHRASE_FILE", "/tmp/passphrase"),
            new KeyValuePair<string, string?>("HOMEHARBOR_RELEASE_PRIVATE_KEY", "/tmp/private.pem"),
            new KeyValuePair<string, string?>("HOMEHARBOR_RELEASE_PUBLIC_KEY", "/tmp/public.pem"),
            new KeyValuePair<string, string?>("OTHER_TOKEN", "ignored")
        };

        var rendered = global::InstallerDiagnostics.FormatInstallerEnvironment(variables);

        Assert.Contains("HOMEHARBOR_DATA_PASSPHRASE_FILE=<redacted>", rendered);
        Assert.Contains("HOMEHARBOR_RELEASE_PRIVATE_KEY=<redacted>", rendered);
        Assert.Contains("HOMEHARBOR_RELEASE_PUBLIC_KEY=/tmp/public.pem", rendered);
        Assert.IsFalse(rendered.Contains("OTHER_TOKEN", StringComparison.Ordinal));
        Assert.IsFalse(rendered.Contains("/tmp/passphrase", StringComparison.Ordinal));
    }

    [TestMethod]
    public void InstallerOptions_Parse_Ignores_Environment_Parameter_Overrides()
    {
        using var _ = new TemporaryEnvironment(
            ("HOMEHARBOR_INSTALLER_MODE", "full"),
            ("HOMEHARBOR_INSTALLER_PAYLOAD_DIR", "/env/payloads"),
            ("HOMEHARBOR_INSTALLER_SYSTEM_OTA", "/env/system-ota.tar.gz"),
            ("HOMEHARBOR_INSTALLER_EXTERNAL_PAYLOAD_DIRS", "/env/removable"),
            ("HOMEHARBOR_RELEASE_PUBLIC_KEY", "/env/release.pub.pem"),
            ("HOMEHARBOR_STABLE_CHANNEL_URL", "https://env.example/stable.json"),
            ("HOMEHARBOR_DAILY_CHANNEL_URL", "https://env.example/daily.json"));

        var options = global::InstallerOptions.Parse([]);

        Assert.AreEqual("tiny", options.Mode);
        Assert.AreEqual(Path.GetFullPath("/opt/homeharbor-installer/payloads"), Path.GetFullPath(options.PayloadDirectory));
        Assert.IsNull(options.SystemOta);
        Assert.AreEqual("/etc/homeharbor/release.pub.pem", options.PublicKey);
        Assert.AreEqual("https://github.com/akaishi-tech/home-harbor/releases/latest/download/channel-stable.json", options.StableChannelUrl);
        Assert.AreEqual("https://github.com/akaishi-tech/home-harbor/releases/download/daily/channel-daily.json", options.DailyChannelUrl);
        CollectionAssert.DoesNotContain(options.ExternalPayloadDirectories.ToArray(), Path.GetFullPath("/env/removable"));
    }

    [TestMethod]
    public void InstallerOptions_Parse_Uses_Explicit_Arguments()
    {
        var options = global::InstallerOptions.Parse([
            "--mode", "full",
            "--payload-dir", "/payloads",
            "--system-ota", "/payloads/homeharbor-system-ota.tar.gz",
            "--external-payload-dir", "/media/usb",
            "--public-key", "/keys/release.pub.pem",
            "--stable-channel-url", "https://example.test/stable.json",
            "--daily-channel-url", "https://example.test/daily.json"
        ]);

        Assert.AreEqual("full", options.Mode);
        Assert.AreEqual(Path.GetFullPath("/payloads"), options.PayloadDirectory);
        Assert.AreEqual("/payloads/homeharbor-system-ota.tar.gz", options.SystemOta);
        Assert.AreEqual("/keys/release.pub.pem", options.PublicKey);
        Assert.AreEqual("https://example.test/stable.json", options.StableChannelUrl);
        Assert.AreEqual("https://example.test/daily.json", options.DailyChannelUrl);
        CollectionAssert.AreEqual(
            new[] { Path.GetFullPath("/payloads"), Path.GetFullPath("/media/usb") },
            options.ExternalPayloadDirectories.ToArray());
    }

    [TestMethod]
    public void HostSecurityState_Builds_Tpm2_Status_Without_Data_Setup_Guidance()
    {
        var noTpm = new global::HostSecurityState(Tpm2Available: false, SecureBootEnabled: false);
        var tpmWithoutSecureBoot = new global::HostSecurityState(Tpm2Available: true, SecureBootEnabled: false);

        var noTpmReport = noTpm.BuildSummary();
        var tpmWithoutSecureBootReport = tpmWithoutSecureBoot.BuildSummary();

        Assert.Contains("TPM2 device: not detected", noTpmReport);
        Assert.Contains("UEFI Secure Boot is disabled or not reported.", noTpmReport);
        Assert.Contains("TPM2 hardware is visible to the live installer.", tpmWithoutSecureBootReport);
        Assert.IsFalse(noTpmReport.Contains("Web OOBE", StringComparison.Ordinal));
        Assert.IsFalse(noTpmReport.Contains("data unlock", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(tpmWithoutSecureBootReport.Contains("Web OOBE", StringComparison.Ordinal));
        Assert.IsFalse(tpmWithoutSecureBootReport.Contains("data unlock", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void InstallConfirmationPrompt_Includes_Exact_Confirmation_Text()
    {
        var prompt = global::TerminalGuiInstallerUi.BuildInstallConfirmationPrompt("ERASE /dev/vda");

        Assert.AreEqual("Type ERASE /dev/vda to install:", prompt);
    }

    [TestMethod]
    public void ReadySummary_Includes_DryRun_And_Log_Without_Secrets()
    {
        var disk = new global::DiskInfo("/dev/vda", 68719476736, "VirtIO Block Device")
        {
            Serial = "disk-a",
            Transport = "virtio"
        };
        var assets = new global::InstallerAssets(
            "0.1.0",
            "stable",
            "/payload/homeharbor-system-ota-0.1.0.tar.gz",
            "/etc/homeharbor/release.pub.pem",
            "/payload/channel-stable.json");
        var dryRun = global::InstallerDryRunResult.FromProcess(0, """
            target=/dev/vda
            version=0.1.0
            channel=stable
            bootMode=raw-uki
            systemOta=/payload/homeharbor-system-ota-0.1.0.tar.gz
            diskSizeBytes=68719476736
            minimumBytes=34359738368
            installWorkMinMiB=24576
            dataSetup=web-oobe
            willEnrollMok=no
            willWritePartitions=esp,boot_a,data-candidate
            """);
        var summary = new global::InstallSummary(
            disk,
            "tiny",
            "stable",
            assets,
            dryRun,
            "/tmp/homeharbor-installer-20260701-120000.log");

        var text = global::TerminalGuiInstallerUi.BuildReadySummary(summary, "ERASE /dev/vda");

        Assert.Contains("Required confirmation: ERASE /dev/vda", text);
        Assert.Contains("Install log:      /tmp/homeharbor-installer-20260701-120000.log", text);
        Assert.Contains("Preflight validation passed.", text);
        Assert.IsFalse(text.Contains("super-secret", StringComparison.Ordinal));
        Assert.IsFalse(text.Contains("Passphrase:", StringComparison.Ordinal));
        Assert.IsFalse(text.Contains("Data setup", StringComparison.Ordinal));
        Assert.IsFalse(text.Contains("Web OOBE", StringComparison.Ordinal));
        Assert.IsFalse(text.Contains("web-oobe", StringComparison.Ordinal));
    }

    [TestMethod]
    public void InstallerAssets_FromSystemOtaFile_Reads_Channel_From_Manifest()
    {
        var temp = Directory.CreateTempSubdirectory("homeharbor-installer-assets-");
        try
        {
            var ota = Path.Combine(temp.FullName, "homeharbor-system-ota-0.1.0.tar.gz");
            WriteSystemOta(ota, "daily");

            var assets = global::InstallerAssets.FromSystemOtaFile(ota, "/etc/homeharbor/release.pub.pem");

            Assert.AreEqual("daily", assets.Channel);
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }

    [TestMethod]
    public void InstallerAssets_FromPayloadDirectory_Reads_Channel_From_Sidecar_Manifest()
    {
        var temp = Directory.CreateTempSubdirectory("homeharbor-installer-assets-");
        try
        {
            var ota = Path.Combine(temp.FullName, "homeharbor-system-ota-0.1.0.tar.gz");
            File.WriteAllText(ota, "not a gzip bundle");
            File.WriteAllText(Path.Combine(temp.FullName, "system-ota-manifest.json"), """
                {
                  "channel": "dev"
                }
                """);

            var assets = global::InstallerAssets.FromPayloadDirectory(temp.FullName, "/etc/homeharbor/release.pub.pem");

            Assert.AreEqual("dev", assets.Channel);
            Assert.AreEqual(ota, assets.SystemOta);
            Assert.AreEqual(Path.Combine(temp.FullName, "system-ota-manifest.json"), assets.ManifestFile);
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }

    [TestMethod]
    public void InstallerAssets_FromPayloadDirectory_Selects_Generic_Kernel_Ota_From_Channel_Metadata()
    {
        var temp = Directory.CreateTempSubdirectory("homeharbor-installer-assets-");
        try
        {
            var systemOta = Path.Combine(temp.FullName, "homeharbor-system-ota-0.1.0.tar.gz");
            var kernelOta = Path.Combine(temp.FullName, "homeharbor-kernel-generic-ota-0.1.0.tar.gz");
            File.WriteAllText(systemOta, "not a gzip bundle");
            File.WriteAllText(kernelOta, "kernel");
            File.WriteAllText(Path.Combine(temp.FullName, "system-ota-manifest.json"), """
                {
                  "channel": "dev"
                }
                """);
            File.WriteAllText(Path.Combine(temp.FullName, "channel-dev.json"), $$"""
                {
                  "channel": "dev",
                  "currentVersion": "0.1.0",
                  "kernelChannels": {
                    "generic": {
                      "bundle": {
                        "file": "0.1.0/homeharbor-kernel-generic-ota-0.1.0.tar.gz",
                        "sha256": "{{Sha256Hex(kernelOta)}}"
                      }
                    }
                  }
                }
                """);

            var assets = global::InstallerAssets.FromPayloadDirectory(temp.FullName, "/etc/homeharbor/release.pub.pem");

            Assert.AreEqual(kernelOta, assets.KernelOta);
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }

    [TestMethod]
    public void InstallerAssets_FromSystemOtaFile_Rejects_Mismatched_Channel_Metadata()
    {
        var temp = Directory.CreateTempSubdirectory("homeharbor-installer-assets-");
        try
        {
            var ota = Path.Combine(temp.FullName, "homeharbor-system-ota-0.1.0.tar.gz");
            WriteSystemOta(ota, "daily");
            File.WriteAllText(Path.Combine(temp.FullName, "channel-stable.json"), """
                {
                  "channel": "stable",
                  "currentVersion": "0.1.0"
                }
                """);

            var ex = Assert.ThrowsExactly<InvalidOperationException>(() =>
                global::InstallerAssets.FromSystemOtaFile(ota, "/etc/homeharbor/release.pub.pem"));
            Assert.Contains("does not match OTA channel", ex.Message);
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }

    private static void AssertCall(IReadOnlyList<CommandCall> calls, string fileName, string[] arguments)
    {
        Assert.Contains(
            call => string.Equals(call.FileName, fileName, StringComparison.Ordinal) &&
                              call.Arguments.SequenceEqual(arguments, StringComparer.Ordinal), calls,
            "Expected command was not recorded: " + fileName + " " + string.Join(" ", arguments));
    }

    private static void WriteSystemOta(string path, string channel)
    {
        using var file = File.Create(path);
        using var gzip = new GZipStream(file, CompressionLevel.SmallestSize);
        using var writer = new TarWriter(gzip);
        var payload = Encoding.UTF8.GetBytes("""
            {
              "channel": "CHANNEL_PLACEHOLDER"
            }
            """.Replace("CHANNEL_PLACEHOLDER", channel, StringComparison.Ordinal));
        var entry = new PaxTarEntry(TarEntryType.RegularFile, "homeharbor-system-ota-0.1.0/manifest.json")
        {
            DataStream = new MemoryStream(payload)
        };
        writer.WriteEntry(entry);
    }

    private static string Sha256Hex(string path)
    {
        using var input = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
    }

    private sealed class RecordingNetworkRunner : ICommandRunner
    {
        public List<CommandCall> Calls { get; } = [];

        public Task<CommandResult> RunAsync(
            string fileName,
            IEnumerable<string> arguments,
            CommandRunOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var args = arguments.ToArray();
            var passwdFileIndex = Array.IndexOf(args, "passwd-file");
            var payload = passwdFileIndex >= 0 && passwdFileIndex + 1 < args.Length && File.Exists(args[passwdFileIndex + 1])
                ? File.ReadAllText(args[passwdFileIndex + 1])
                : string.Empty;
            Calls.Add(new CommandCall(fileName, args, payload));
            return Task.FromResult(new CommandResult(0, string.Empty, string.Empty, fileName + " " + string.Join(" ", args)));
        }
    }

    private sealed record CommandCall(string FileName, string[] Arguments, string Payload);

    private sealed class TemporaryEnvironment : IDisposable
    {
        private readonly (string Name, string? Value)[] previous;

        public TemporaryEnvironment(params (string Name, string? Value)[] values)
        {
            previous = [.. values.Select(value => (value.Name, Environment.GetEnvironmentVariable(value.Name)))];
            foreach (var (name, value) in values)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }

        public void Dispose()
        {
            foreach (var (name, value) in previous)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }
    }
}
