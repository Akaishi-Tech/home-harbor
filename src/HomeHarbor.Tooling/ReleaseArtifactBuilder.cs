using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HomeHarbor.Tooling;

public sealed record ReleaseArtifactBuildResult(
    string ReleaseDirectory,
    string SystemOta,
    string KernelOta,
    string ChannelFile,
    string LatestChannelFile,
    string InstallerIso);

public sealed class ReleaseArtifactBuilder(
    string root,
    string version,
    SystemImageBuildPlan plan,
    ICommandRunner? runner = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _root = BuildKeyDefaults.Apply(root);
    private readonly ICommandRunner _runner = runner ?? new ProcessCommandRunner();
    private readonly string _work = Path.Combine(Path.GetFullPath(root), ".work", "release", version);
    private readonly string _imageWork = Path.Combine(Path.GetFullPath(root), ".work", "image");
    private readonly string _releaseRoot = Path.Combine(Path.GetFullPath(root), "artifacts", "channels");
    private readonly string _releaseDirectory = Path.Combine(Path.GetFullPath(root), "artifacts", "channels", version);

    public async Task<ReleaseArtifactBuildResult> BuildAsync(CancellationToken cancellationToken = default)
    {
        var channel = ReleaseChannel.Require(
            Env.String("HOMEHARBOR_RELEASE_CHANNEL", Env.String("HOMEHARBOR_ISO_CHANNEL", Env.String("HOMEHARBOR_CHANNEL", ReleaseChannel.Dev))),
            "release channel");
        const string kernelChannel = "generic";
        var createdAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

        await RequireToolsAsync(["openssl", "repo-add", "mkarchiso"], cancellationToken);
        DeleteIfExists(_work);
        _ = Directory.CreateDirectory(_work);
        DeleteIfExists(_releaseDirectory);
        _ = Directory.CreateDirectory(_releaseDirectory);
        _ = Directory.CreateDirectory(_releaseRoot);

        var keys = await ResolveReleaseKeysAsync(channel, cancellationToken);
        await CopyPackagesAsync(cancellationToken);

        var systemOta = await BuildSystemOtaAsync(channel, createdAt, keys, cancellationToken);
        var kernelOta = await BuildKernelOtaAsync(channel, kernelChannel, createdAt, keys, cancellationToken);
        var payloadChannelFile = await WriteChannelMetadataAsync(
            Path.Combine(_releaseDirectory, "channel-" + channel + ".json"),
            channel,
            systemOta,
            kernelChannel,
            kernelOta,
            installerIso: null,
            cancellationToken);

        var iso = await BuildFullInstallerIsoAsync(channel, systemOta, kernelOta, payloadChannelFile, keys.PublicKey, cancellationToken);
        var channelFile = await WriteChannelMetadataAsync(
            Path.Combine(_releaseDirectory, "channel-" + channel + ".json"),
            channel,
            systemOta,
            kernelChannel,
            kernelOta,
            iso,
            cancellationToken);
        var latestChannelFile = await WriteChannelMetadataAsync(
            Path.Combine(_releaseRoot, "channel.json"),
            channel,
            systemOta,
            kernelChannel,
            kernelOta,
            iso,
            cancellationToken);

        Console.WriteLine("Built release artifacts in " + _releaseDirectory);
        Console.WriteLine(iso);
        return new ReleaseArtifactBuildResult(_releaseDirectory, systemOta, kernelOta, channelFile, latestChannelFile, iso);
    }

    private async Task<string> BuildSystemOtaAsync(
        string channel,
        string createdAt,
        ReleaseKeys keys,
        CancellationToken cancellationToken)
    {
        var top = "homeharbor-system-ota-" + version;
        var root = Path.Combine(_work, "system-ota", top);
        _ = Directory.CreateDirectory(root);

        await CopyReleaseFileAsync(plan.Artifacts.Rootfs.Path, Path.Combine(root, "rootfs.img"), cancellationToken);
        await CopyShaAsync(plan.Artifacts.Rootfs.Path, Path.Combine(root, "rootfs.img.sha256"), cancellationToken);
        await CopyReleaseFileAsync(plan.Artifacts.VbmetaA.Path, Path.Combine(root, "vbmeta_a.img"), cancellationToken);
        await CopyShaAsync(plan.Artifacts.VbmetaA.Path, Path.Combine(root, "vbmeta_a.img.sha256"), cancellationToken);
        await CopyReleaseFileAsync(plan.Artifacts.VbmetaB.Path, Path.Combine(root, "vbmeta_b.img"), cancellationToken);
        await CopyShaAsync(plan.Artifacts.VbmetaB.Path, Path.Combine(root, "vbmeta_b.img.sha256"), cancellationToken);

        var manifest = new JsonObject
        {
            ["schemaVersion"] = "1",
            ["type"] = "full-system",
            ["packageKind"] = "system",
            ["version"] = version,
            ["channel"] = channel,
            ["createdAt"] = createdAt,
            ["targetSlot"] = "A",
            ["bootMode"] = SecureBootAssets.BootMode(),
            ["rootfsHash"] = await Sha256HexAsync(plan.Artifacts.Rootfs.Path, cancellationToken),
            ["vbmetaAHash"] = await Sha256HexAsync(plan.Artifacts.VbmetaA.Path, cancellationToken),
            ["vbmetaBHash"] = await Sha256HexAsync(plan.Artifacts.VbmetaB.Path, cancellationToken),
            ["vbmetaADigest"] = await ReadDigestAsync(plan.Artifacts.VbmetaA, cancellationToken),
            ["vbmetaBDigest"] = await ReadDigestAsync(plan.Artifacts.VbmetaB, cancellationToken)
        };
        await WriteSignedManifestAsync(Path.Combine(root, "manifest.json"), manifest, keys, cancellationToken);

        var bundle = Path.Combine(_releaseDirectory, top + ".tar.gz");
        await CreateTarGzAsync(Path.GetDirectoryName(root)!, top, bundle, cancellationToken);
        return bundle;
    }

    private async Task<string> BuildKernelOtaAsync(
        string channel,
        string kernelChannel,
        string createdAt,
        ReleaseKeys keys,
        CancellationToken cancellationToken)
    {
        var top = "homeharbor-kernel-" + kernelChannel + "-ota-" + version;
        var root = Path.Combine(_work, "kernel-ota", top);
        _ = Directory.CreateDirectory(root);

        await CopyReleaseFileAsync(plan.Artifacts.Modules.Path, Path.Combine(root, "modules.img"), cancellationToken);
        await CopyShaAsync(plan.Artifacts.Modules.Path, Path.Combine(root, "modules.img.sha256"), cancellationToken);
        await CopyReleaseFileAsync(plan.Artifacts.Firmware.Path, Path.Combine(root, "firmware.img"), cancellationToken);
        await CopyShaAsync(plan.Artifacts.Firmware.Path, Path.Combine(root, "firmware.img.sha256"), cancellationToken);
        await CopyReleaseFileAsync(plan.Artifacts.Recovery.Path, Path.Combine(root, "recovery.img"), cancellationToken);
        await CopyShaAsync(plan.Artifacts.Recovery.Path, Path.Combine(root, "recovery.img.sha256"), cancellationToken);
        await CopyReleaseFileAsync(plan.Artifacts.Boot.Path, Path.Combine(root, "boot.efi"), cancellationToken);
        await CopyShaAsync(plan.Artifacts.Boot.Path, Path.Combine(root, "boot.efi.sha256"), cancellationToken);

        await CopyOptionalReleaseFileWithShaAsync(
            plan.Artifacts.Bootloader.Path,
            Path.Combine(root, "HomeHarborBoot.efi"),
            Path.Combine(root, "HomeHarborBoot.efi.sha256"),
            cancellationToken);
        await CopyOptionalReleaseFileWithShaAsync(
            plan.Artifacts.Bootx64.Path,
            Path.Combine(root, "BOOTX64.EFI"),
            Path.Combine(root, "BOOTX64.EFI.sha256"),
            cancellationToken);
        await WriteVeritySidecarsAsync(root, cancellationToken);

        var manifest = new JsonObject
        {
            ["schemaVersion"] = "1",
            ["type"] = "kernel-only",
            ["packageKind"] = "kernel",
            ["version"] = version,
            ["channel"] = channel,
            ["kernelChannel"] = kernelChannel,
            ["createdAt"] = createdAt,
            ["targetSlot"] = "A",
            ["bootMode"] = SecureBootAssets.BootMode(),
            ["kernelRelease"] = ResolveKernelRelease(),
            ["modulesHash"] = await Sha256HexAsync(plan.Artifacts.Modules.Path, cancellationToken),
            ["firmwareHash"] = await Sha256HexAsync(plan.Artifacts.Firmware.Path, cancellationToken),
            ["recoveryHash"] = await Sha256HexAsync(plan.Artifacts.Recovery.Path, cancellationToken),
            ["bootHash"] = await Sha256HexAsync(plan.Artifacts.Boot.Path, cancellationToken)
        };
        if (File.Exists(plan.Artifacts.Bootloader.Path))
        {
            manifest["bootloaderHash"] = await Sha256HexAsync(plan.Artifacts.Bootloader.Path, cancellationToken);
        }

        if (File.Exists(plan.Artifacts.Bootx64.Path))
        {
            manifest["fallbackBootHash"] = await Sha256HexAsync(plan.Artifacts.Bootx64.Path, cancellationToken);
        }

        await WriteSignedManifestAsync(Path.Combine(root, "manifest.json"), manifest, keys, cancellationToken);

        var bundle = Path.Combine(_releaseDirectory, top + ".tar.gz");
        await CreateTarGzAsync(Path.GetDirectoryName(root)!, top, bundle, cancellationToken);
        return bundle;
    }

    private async Task<string> BuildFullInstallerIsoAsync(
        string channel,
        string systemOta,
        string kernelOta,
        string channelFile,
        string publicKey,
        CancellationToken cancellationToken)
    {
        var packageDirectory = Path.Combine(_releaseDirectory, "packages");
        await RunAsync("repo-add", ["homeharbor-local.db.tar.gz", .. Directory.GetFiles(packageDirectory, "*.pkg.tar.*").Select(Path.GetFileName)!], cancellationToken, packageDirectory);

        var profile = Path.Combine(_work, "iso-profile");
        CopyDirectory("/usr/share/archiso/configs/baseline", profile);
        await PrepareIsoProfileAsync(profile, packageDirectory, systemOta, kernelOta, channelFile, publicKey, channel, cancellationToken);

        var isoOut = Path.Combine(_work, "iso-out");
        var isoWork = Path.Combine(_work, "mkarchiso");
        _ = Directory.CreateDirectory(isoOut);
        await RunMkarchisoAsync(isoWork, isoOut, profile, cancellationToken);

        var builtIso = Directory.GetFiles(isoOut, "*.iso", SearchOption.TopDirectoryOnly)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault()
            ?? throw new InvalidOperationException("mkarchiso did not produce an ISO in " + isoOut);
        var finalIso = Path.Combine(_releaseDirectory, "homeharbor-full-live-installer-" + channel + "-" + version + ".iso");
        File.Move(builtIso, finalIso, overwrite: true);
        await WriteShaFileAsync(finalIso, finalIso + ".sha256", cancellationToken);
        return finalIso;
    }

    private async Task PrepareIsoProfileAsync(
        string profile,
        string packageDirectory,
        string systemOta,
        string kernelOta,
        string channelFile,
        string publicKey,
        string channel,
        CancellationToken cancellationToken)
    {
        var profileDef = $$"""
            #!/usr/bin/env bash
            # shellcheck disable=SC2034

            iso_name="homeharbor-full-live-installer-{{channel}}"
            iso_label="{{IsoLabel(channel, version)}}"
            iso_publisher="HomeHarbor <https://github.com/akaishi-tech/home-harbor>"
            iso_application="HomeHarbor Full Live Installer"
            iso_version="{{version}}"
            install_dir="hh"
            buildmodes=('iso')
            bootmodes=('bios.syslinux'
                       'uefi.grub')
            pacman_conf="pacman.conf"
            airootfs_image_type="erofs"
            airootfs_image_tool_options=('-zlzma,109' -E 'ztailpacking')
            bootstrap_tarball_compression=(xz -9e)
            file_permissions=(
              ["/etc/shadow"]="0:0:400"
              ["/root"]="0:0:750"
              ["/root/.bash_profile"]="0:0:700"
              ["/root/customize_airootfs.sh"]="0:0:755"
            )
            """;
        await FileWrites.AtomicWriteTextAsync(Path.Combine(profile, "profiledef.sh"), profileDef, 0755, cancellationToken);

        var packages = (await File.ReadAllLinesAsync(Path.Combine(profile, "packages.x86_64"), cancellationToken))
            .Concat(["homeharbor-installer", "networkmanager", "systemd-resolvconf", "gpm"])
            .Where(line => !string.IsNullOrWhiteSpace(line) && !line.TrimStart().StartsWith('#'))
            .Select(line => line.Trim())
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        await FileWrites.AtomicWriteTextAsync(Path.Combine(profile, "packages.x86_64"), string.Join('\n', packages) + "\n", cancellationToken: cancellationToken);

        await File.AppendAllTextAsync(
            Path.Combine(profile, "pacman.conf"),
            "\n[homeharbor-local]\nSigLevel = Optional TrustAll\nServer = file://" + packageDirectory + "\n",
            cancellationToken);

        var payloadDirectory = Path.Combine(profile, "airootfs", "opt", "homeharbor-installer", "payloads");
        _ = Directory.CreateDirectory(payloadDirectory);
        await CopyReleaseFileAsync(systemOta, Path.Combine(payloadDirectory, Path.GetFileName(systemOta)), cancellationToken);
        await CopyReleaseFileAsync(kernelOta, Path.Combine(payloadDirectory, Path.GetFileName(kernelOta)), cancellationToken);
        await CopyReleaseFileAsync(channelFile, Path.Combine(payloadDirectory, Path.GetFileName(channelFile)), cancellationToken);
        await CopyReleaseFileAsync(publicKey, Path.Combine(payloadDirectory, "release.pub.pem"), cancellationToken);

        await WriteLiveInstallerStartupAsync(profile, cancellationToken);
    }

    private static async Task WriteLiveInstallerStartupAsync(string profile, CancellationToken cancellationToken)
    {
        var ttyDropIn = Path.Combine(profile, "airootfs", "etc", "systemd", "system", "getty@tty1.service.d", "autologin.conf");
        await FileWrites.AtomicWriteTextAsync(ttyDropIn, """
            [Service]
            ExecStart=
            ExecStart=-/usr/bin/agetty --noreset --noclear --autologin root - ${TERM}
            """.Replace("            ", string.Empty, StringComparison.Ordinal), cancellationToken: cancellationToken);

        var serialDropIn = Path.Combine(profile, "airootfs", "etc", "systemd", "system", "serial-getty@ttyS0.service.d", "autologin.conf");
        await FileWrites.AtomicWriteTextAsync(serialDropIn, """
            [Service]
            ExecStart=
            ExecStart=-/usr/bin/agetty --autologin root --keep-baud 115200,57600,38400,9600 %I ${TERM}
            """.Replace("            ", string.Empty, StringComparison.Ordinal), cancellationToken: cancellationToken);

        await FileWrites.AtomicWriteTextAsync(Path.Combine(profile, "airootfs", "root", ".bash_profile"), """
            if [ -z "${HOMEHARBOR_INSTALLER_STARTED:-}" ]; then
              case "$(tty)" in
                /dev/tty1|/dev/ttyS0)
                  export HOMEHARBOR_INSTALLER_STARTED=1
                  export HOMEHARBOR_INSTALLER_TUI_COLOR_MODE=kernel-16
                  export HOMEHARBOR_INSTALLER_TUI_SIZE_DETECTION=polling
                  printf '\nHomeHarbor Live Installer\n\n'
                  exec /usr/lib/homeharbor/installer/HomeHarbor.Installer --mode full --payload-dir /opt/homeharbor-installer/payloads
                  ;;
              esac
            fi
            """.Replace("            ", string.Empty, StringComparison.Ordinal), 0700, cancellationToken);

        await FileWrites.AtomicWriteTextAsync(Path.Combine(profile, "airootfs", "root", "customize_airootfs.sh"), """
            #!/usr/bin/env bash
            set -euo pipefail
            systemctl enable serial-getty@ttyS0.service
            systemctl enable getty@tty1.service
            systemctl enable systemd-networkd.service systemd-resolved.service qemu-guest-agent.service
            """.Replace("            ", string.Empty, StringComparison.Ordinal), 0755, cancellationToken);
    }

    private async Task<string> WriteChannelMetadataAsync(
        string path,
        string channel,
        string systemOta,
        string kernelChannel,
        string kernelOta,
        string? installerIso,
        CancellationToken cancellationToken)
    {
        var root = new JsonObject
        {
            ["schemaVersion"] = "1",
            ["channel"] = channel,
            ["currentVersion"] = version,
            ["generatedAt"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            ["systemOta"] = new JsonObject
            {
                ["bundle"] = BundleNode(systemOta)
            },
            ["kernelChannels"] = new JsonObject
            {
                [kernelChannel] = new JsonObject
                {
                    ["bundle"] = BundleNode(kernelOta)
                }
            }
        };

        if (!string.IsNullOrWhiteSpace(installerIso))
        {
            root["installerIsos"] = new JsonObject
            {
                ["full"] = BundleNode(installerIso)
            };
        }

        await FileWrites.AtomicWriteTextAsync(path, root.ToJsonString(JsonOptions) + "\n", cancellationToken: cancellationToken);
        return path;
    }

    private JsonObject BundleNode(string path)
        => new()
        {
            ["file"] = version + "/" + Path.GetFileName(path),
            ["sha256"] = Sha256Hex(path)
        };

    private async Task WriteSignedManifestAsync(
        string path,
        JsonObject manifest,
        ReleaseKeys keys,
        CancellationToken cancellationToken)
    {
        var unsigned = manifest.ToJsonString();
        using var doc = JsonDocument.Parse(unsigned);
        var payload = OtaManifestVerifier.CanonicalPayload(doc.RootElement);
        var payloadSha = Sha256Hex(Encoding.UTF8.GetBytes(payload));
        var payloadPath = Path.Combine(_work, "manifest-payload-" + Guid.NewGuid().ToString("N") + ".json");
        var signaturePath = Path.Combine(_work, "manifest-signature-" + Guid.NewGuid().ToString("N") + ".bin");
        await File.WriteAllTextAsync(payloadPath, payload, cancellationToken);
        try
        {
            await RunAsync("openssl", ["pkeyutl", "-sign", "-inkey", keys.PrivateKey, "-rawin", "-in", payloadPath, "-out", signaturePath], cancellationToken);
            manifest["signatureAlgorithm"] = "Ed25519";
            manifest["signedPayloadSha256"] = payloadSha;
            manifest["signingKeyId"] = keys.KeyId;
            manifest["signature"] = Convert.ToBase64String(await File.ReadAllBytesAsync(signaturePath, cancellationToken));
            await FileWrites.AtomicWriteTextAsync(path, manifest.ToJsonString(JsonOptions) + "\n", 0644, cancellationToken);
        }
        finally
        {
            TryDeleteFile(payloadPath);
            TryDeleteFile(signaturePath);
        }
    }

    private async Task WriteVeritySidecarsAsync(string root, CancellationToken cancellationToken)
    {
        foreach (var partition in new[] { "modules_a", "modules_b", "firmware_a", "firmware_b", "recovery_a", "recovery_b" })
        {
            var vbmeta = Path.Combine(_imageWork, partition + ".vbmeta");
            if (!File.Exists(vbmeta))
            {
                throw new FileNotFoundException("verity sidecar source is missing; run system-build before release-build", vbmeta);
            }

            var output = await CaptureAsync(AvbHelperPath(), ["descriptor", vbmeta, AvbPartitionNames.DescriptorName(partition)], cancellationToken);
            var values = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Split('=', 2))
                .Where(parts => parts.Length == 2)
                .ToDictionary(parts => parts[0].Trim(), parts => parts[1].Trim(), StringComparer.Ordinal);
            var verity = string.Join(
                ':',
                Descriptor(values, "HASH_ALGORITHM"),
                Descriptor(values, "DATA_BLOCK_SIZE"),
                Descriptor(values, "HASH_BLOCK_SIZE"),
                Descriptor(values, "DATA_BLOCKS"),
                Descriptor(values, "TREE_OFFSET"),
                Descriptor(values, "SALT"),
                Descriptor(values, "ROOT_DIGEST"));
            await FileWrites.AtomicWriteTextAsync(Path.Combine(root, partition + ".verity"), verity + "\n", 0644, cancellationToken);
        }
    }

    private async Task CopyPackagesAsync(CancellationToken cancellationToken)
    {
        var source = Directory.Exists(Path.Combine(_imageWork, "packages"))
            ? Path.Combine(_imageWork, "packages")
            : Path.Combine(_root, "artifacts", "packages", version);
        if (!Directory.Exists(source))
        {
            throw new DirectoryNotFoundException("HomeHarbor package directory not found; run system-build first: " + source);
        }

        var destination = Path.Combine(_releaseDirectory, "packages");
        _ = Directory.CreateDirectory(destination);
        foreach (var package in Directory.GetFiles(source, "*.pkg.tar.*", SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal))
        {
            await CopyReleaseFileAsync(package, Path.Combine(destination, Path.GetFileName(package)), cancellationToken);
        }

        if (Directory.GetFiles(destination, "homeharbor-installer-*.pkg.tar.*").Length == 0)
        {
            throw new InvalidOperationException("release package set is missing homeharbor-installer");
        }
    }

    private async Task<ReleaseKeys> ResolveReleaseKeysAsync(string channel, CancellationToken cancellationToken)
    {
        var privateKey = Env.Optional("HOMEHARBOR_RELEASE_PRIVATE_KEY");
        var publicKey = Env.Optional("HOMEHARBOR_RELEASE_PUBLIC_KEY");
        if (!string.IsNullOrWhiteSpace(privateKey) && !string.IsNullOrWhiteSpace(publicKey))
        {
            RequireFile(privateKey, "release private key not found");
            RequireFile(publicKey, "release public key not found");
            return new ReleaseKeys(privateKey, publicKey, Env.String("HOMEHARBOR_RELEASE_KEY_ID", channel + "-local"));
        }

        if (channel != ReleaseChannel.Dev)
        {
            throw new InvalidOperationException("HOMEHARBOR_RELEASE_PRIVATE_KEY and HOMEHARBOR_RELEASE_PUBLIC_KEY are required for " + channel + " releases");
        }

        var keyDir = Path.Combine(_work, "dev-release-key");
        _ = Directory.CreateDirectory(keyDir);
        privateKey = Path.Combine(keyDir, "release.pem");
        publicKey = Path.Combine(keyDir, "release.pub.pem");
        await RunAsync("openssl", ["genpkey", "-algorithm", "Ed25519", "-out", privateKey], cancellationToken);
        await RunAsync("openssl", ["pkey", "-in", privateKey, "-pubout", "-out", publicKey], cancellationToken);
        await RunAsync("chmod", ["0600", privateKey], cancellationToken);
        await RunAsync("chmod", ["0644", publicKey], cancellationToken);
        return new ReleaseKeys(privateKey, publicKey, "dev-local");
    }

    private async Task RunMkarchisoAsync(string work, string output, string profile, CancellationToken cancellationToken)
    {
        var command = Environment.UserName == "root" ? "mkarchiso" : "sudo";
        var args = Environment.UserName == "root"
            ? new[] { "-v", "-w", work, "-o", output, profile }
            : ["-n", "mkarchiso", "-v", "-w", work, "-o", output, profile];
        await RunAsync(command, args, cancellationToken, timeout: TimeSpan.FromHours(3));
    }

    private async Task RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken,
        string? workingDirectory = null,
        TimeSpan? timeout = null)
    {
        var result = await _runner.RunAsync(
            fileName,
            arguments,
            new CommandRunOptions(WorkingDirectory: workingDirectory, Timeout: timeout, StreamOutput: true, StreamError: true),
            cancellationToken);
        _ = result.EnsureSuccess();
    }

    private async Task<string> CaptureAsync(string fileName, IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        var result = await _runner.RunAsync(fileName, arguments, cancellationToken: cancellationToken);
        _ = result.EnsureSuccess();
        return result.Stdout;
    }

    private async Task RequireToolsAsync(IEnumerable<string> tools, CancellationToken cancellationToken)
    {
        foreach (var tool in tools)
        {
            var result = await _runner.RunAsync("sh", ["-c", "command -v \"$1\" >/dev/null", "sh", tool], cancellationToken: cancellationToken);
            _ = result.EnsureSuccess("missing required tool: " + tool);
        }
    }

    private string ResolveKernelRelease()
    {
        foreach (var candidate in new[]
                 {
                     Path.Combine(_imageWork, "modules-root", "usr", "lib", "modules"),
                     Path.Combine(_imageWork, "recovery-rootfs", "usr", "lib", "modules"),
                     Path.Combine(_imageWork, "rootfs", "usr", "lib", "modules")
                 })
        {
            if (!Directory.Exists(candidate))
            {
                continue;
            }

            var releases = Directory.GetDirectories(candidate).Select(Path.GetFileName).Where(name => !string.IsNullOrWhiteSpace(name)).Order(StringComparer.Ordinal).ToArray();
            if (releases.Length == 1)
            {
                return releases[0]!;
            }
        }

        throw new InvalidOperationException("could not resolve kernel release from build work directory");
    }

    private string AvbHelperPath()
    {
        foreach (var candidate in new[]
                 {
                     Path.Combine(_root, "artifacts", "homeharbor-avb"),
                     Path.Combine(_imageWork, "homeharbor-avb")
                 })
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("homeharbor-avb helper not found; run system-build first");
    }

    private static async Task CreateTarGzAsync(string sourceDirectory, string topDirectory, string output, CancellationToken cancellationToken)
    {
        await using var file = File.Create(output);
        await using var gzip = new GZipStream(file, CompressionLevel.SmallestSize);
        await using var writer = new TarWriter(gzip);
        foreach (var path in Directory.EnumerateFileSystemEntries(Path.Combine(sourceDirectory, topDirectory), "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(sourceDirectory, path).Replace(Path.DirectorySeparatorChar, '/');
            if (Directory.Exists(path))
            {
                writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, relative + "/"));
                continue;
            }

            var entry = new PaxTarEntry(TarEntryType.RegularFile, relative)
            {
                DataStream = File.OpenRead(path)
            };
            writer.WriteEntry(entry);
            await entry.DataStream.DisposeAsync();
        }

        _ = cancellationToken;
    }

    private static void CopyDirectory(string source, string destination)
    {
        var sourceInfo = new DirectoryInfo(source);
        if (!sourceInfo.Exists)
        {
            throw new DirectoryNotFoundException("directory not found: " + source);
        }

        _ = Directory.CreateDirectory(destination);
        CopyDirectoryContents(sourceInfo, destination);
    }

    private static void CopyDirectoryContents(DirectoryInfo source, string destination)
    {
        foreach (var entry in source.EnumerateFileSystemInfos().OrderBy(entry => entry.Name, StringComparer.Ordinal))
        {
            var output = Path.Combine(destination, entry.Name);
            if (!string.IsNullOrEmpty(entry.LinkTarget))
            {
                DeleteIfExists(output);
                _ = entry.Attributes.HasFlag(FileAttributes.Directory)
                    ? Directory.CreateSymbolicLink(output, entry.LinkTarget)
                    : File.CreateSymbolicLink(output, entry.LinkTarget);

                continue;
            }

            if (entry.Attributes.HasFlag(FileAttributes.Directory))
            {
                _ = Directory.CreateDirectory(output);
                CopyDirectoryContents((DirectoryInfo)entry, output);
                continue;
            }

            _ = Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            File.Copy(entry.FullName, output, overwrite: true);
        }
    }

    private static async Task CopyReleaseFileAsync(string source, string destination, CancellationToken cancellationToken)
    {
        RequireFile(source, "release input not found");
        await FileWrites.CopyFileAsync(source, destination, cancellationToken: cancellationToken);
    }

    internal static async Task CopyOptionalReleaseFileWithShaAsync(
        string source,
        string destination,
        string shaDestination,
        CancellationToken cancellationToken)
    {
        if (File.Exists(source))
        {
            await FileWrites.CopyFileAsync(source, destination, cancellationToken: cancellationToken);
            await WriteShaFileAsync(source, shaDestination, cancellationToken);
        }
    }

    private static async Task CopyShaAsync(string source, string destination, CancellationToken cancellationToken)
        => await WriteShaFileAsync(source, destination, cancellationToken);

    private static async Task WriteShaFileAsync(string source, string destination, CancellationToken cancellationToken)
        => await FileWrites.AtomicWriteTextAsync(destination, await Sha256HexAsync(source, cancellationToken) + "  " + Path.GetFileName(source) + "\n", 0644, cancellationToken);

    private static async Task<string> ReadDigestAsync(SystemImageArtifactPlan artifact, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(artifact.DigestPath) || !File.Exists(artifact.DigestPath))
        {
            throw new FileNotFoundException("vbmeta digest artifact not found", artifact.DigestPath);
        }

        var digest = (await File.ReadAllTextAsync(artifact.DigestPath, cancellationToken))
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault() ?? string.Empty;
        return digest.Length == 64 && digest.All(Uri.IsHexDigit)
            ? digest.ToLowerInvariant()
            : throw new InvalidOperationException("invalid vbmeta digest: " + artifact.DigestPath);
    }

    private static async Task<string> Sha256HexAsync(string path, CancellationToken cancellationToken)
    {
        await using var input = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken)).ToLowerInvariant();
    }

    private static string Sha256Hex(string path)
    {
        using var input = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
    }

    private static string Sha256Hex(byte[] payload)
        => Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();

    private static string Descriptor(IReadOnlyDictionary<string, string> values, string name)
        => values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidOperationException("AVB descriptor output is missing " + name);

    private static void RequireFile(string path, string label)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(label + ": " + path, path);
        }
    }

    private static void DeleteIfExists(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
        else if (File.Exists(path))
        {
            File.Delete(path);
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

    private static string IsoLabel(string channel, string version)
    {
        var value = ("HH_" + channel + "_" + version).ToUpperInvariant();
        var builder = new StringBuilder();
        foreach (var c in value)
        {
            _ = builder.Append(char.IsAsciiLetterOrDigit(c) ? c : '_');
        }

        return builder.Length <= 32 ? builder.ToString() : builder.ToString(0, 32);
    }

    private sealed record ReleaseKeys(string PrivateKey, string PublicKey, string KeyId);
}
