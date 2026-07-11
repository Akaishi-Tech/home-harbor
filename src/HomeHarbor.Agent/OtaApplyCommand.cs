using System.CommandLine;
using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HomeHarbor.Tooling;

internal static class OtaApplyCommand
{
    private const long MaxOtaManifestBytes = 1 * 1024 * 1024;
    private const long MaxUnsignedManifestScanBytes = 16 * 1024 * 1024;
    private const long MaxOtaBundleFileBytes = 4L * 1024 * 1024 * 1024;
    private const long MaxOtaBundleTotalBytes = 8L * 1024 * 1024 * 1024;
    private const int MaxOtaBundleMembers = 256;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static Command CreateCommand(ICommandRunner runner)
    {
        var options = OtaApplyOptions.CreateCommandOptions();
        var command = new Command("ota-apply", "Apply a HomeHarbor OTA bundle.");
        options.AddTo(command);
        command.SetAction(async (parseResult, cancellationToken) =>
            await RunAsync(OtaApplyOptions.FromParseResult(parseResult, options), runner, cancellationToken));
        return command;
    }

    public static async Task<int> RunAsync(string[] args, ICommandRunner runner, CancellationToken cancellationToken)
    {
        if (args.Any(arg => arg is "-h" or "--help"))
        {
            Console.WriteLine("""
                Usage: HomeHarbor.Agent ota-apply <homeharbor-*-ota-version.tar.gz> [options]

                Options:
                  --public-key PATH
                  --state-dir PATH
                  --work-dir PATH
                  --channel CHANNEL
                  --channel-file PATH
                  --kernel-channel CHANNEL
                  --kernel-channel-file PATH
                  --esp PATH
                  --boot-env PATH
                  --current-os-release PATH
                  --current-cmdline PATH
                  --allow-sequence-bootstrap
                  --dry-run
                  --no-reboot
                  --verify-script PATH
                  --active-slot A|B
                  --active-boot-slot A|B
                  --active-root-slot A|B
                  --super-device PATH
                  --boot-device PATH
                  --recovery-device PATH
                  --vbmeta-device PATH
                  --current-crypttab PATH
                  --target-crypttab PATH
                  --data-unlock-metadata PATH
                """);
            return 0;
        }

        return await RunAsync(OtaApplyOptions.Parse(args), runner, cancellationToken);
    }

    private static async Task<int> RunAsync(OtaApplyOptions options, ICommandRunner runner, CancellationToken cancellationToken)
    {
        if (!File.Exists(options.Bundle))
        {
            throw new FileNotFoundException("OTA bundle not found: " + options.Bundle, options.Bundle);
        }

        if (!File.Exists(options.PublicKey))
        {
            throw new FileNotFoundException("release public key not found: " + options.PublicKey, options.PublicKey);
        }

        var work = CreateWorkDirectory(options.WorkDirectory);
        var createdMaps = new List<string>();
        var rootfsMounted = false;
        try
        {
            var verifiedManifest = await ExtractManifestForVerificationAsync(options.Bundle, work, cancellationToken);
            await VerifyManifestAsync(runner, verifiedManifest.Path, options, cancellationToken);

            var bundleTop = await ExtractBundleAsync(options.Bundle, work, cancellationToken);
            if (!string.Equals(bundleTop, verifiedManifest.TopDirectory, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("OTA bundle top-level directory changed after manifest verification");
            }

            var bundleRoot = Path.Combine(work, bundleTop);
            var manifestPath = Path.Combine(bundleRoot, "manifest.json");
            if (!File.Exists(manifestPath))
            {
                throw new InvalidOperationException("OTA bundle does not contain manifest.json");
            }

            if (!string.Equals(Sha256File(manifestPath), verifiedManifest.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("OTA bundle manifest changed after signature verification");
            }

            using var manifestDoc = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath, cancellationToken));
            var plan = await BuildPlanAsync(runner, options, bundleRoot, manifestDoc.RootElement, work, cancellationToken);
            if (options.DryRun)
            {
                WriteDryRun(plan);
                return 0;
            }

            if (plan.Type == OtaType.KernelOnly)
            {
                OtaEspBootAssetInstaller.ValidateKernelAssets(
                    options.Esp,
                    Path.Combine(bundleRoot, "HomeHarborBoot.efi"),
                    plan.BootloaderHash,
                    Path.Combine(bundleRoot, "BOOTX64.EFI"),
                    plan.FallbackBootHash,
                    Path.Combine(bundleRoot, "mmx64.efi"),
                    plan.MokManagerHash,
                    plan.BootMode);
            }

            if (!await IsBlockDeviceAsync(runner, plan.SuperDevice, cancellationToken))
            {
                throw new InvalidOperationException("target Android super block device is missing: " + plan.SuperDevice);
            }

            _ = Directory.CreateDirectory(options.StateDir);
            var mapper = new SuperMapper(runner);
            var rootDevice = string.Empty;
            var modulesDevice = string.Empty;
            var firmwareDevice = string.Empty;
            if (plan.Type == OtaType.FullSystem)
            {
                rootDevice = "/dev/mapper/" + plan.RootMapName;
                await mapper.CreateAsync(plan.RootMapName, plan.SuperDevice, plan.RootLogical, "rw", cancellationToken);
                createdMaps.Add(plan.RootMapName);
            }
            else
            {
                modulesDevice = "/dev/mapper/" + plan.ModulesMapName;
                firmwareDevice = "/dev/mapper/" + plan.FirmwareMapName;
                await mapper.CreateAsync(plan.ModulesMapName, plan.SuperDevice, plan.ModulesLogical, "rw", cancellationToken);
                createdMaps.Add(plan.ModulesMapName);
                await mapper.CreateAsync(plan.FirmwareMapName, plan.SuperDevice, plan.FirmwareLogical, "rw", cancellationToken);
                createdMaps.Add(plan.FirmwareMapName);
            }

            if (plan.Type == OtaType.FullSystem)
            {
                await WriteCompletePartitionImageAsync(
                    runner,
                    plan.RootfsPath,
                    rootDevice,
                    "root " + plan.TargetRootSlot,
                    cancellationToken);
                await WriteRawPartitionImageAsync(runner, Path.Combine(bundleRoot, "vbmeta_" + SlotLower(plan.TargetRootSlot) + ".img"), plan.VbmetaDevice, "vbmeta " + plan.TargetRootSlot, cancellationToken);
            }
            else
            {
                await WriteCompletePartitionImageAsync(
                    runner,
                    Path.Combine(bundleRoot, "modules.img"),
                    modulesDevice,
                    "modules " + plan.TargetBootSlot,
                    cancellationToken);
                await WriteCompletePartitionImageAsync(
                    runner,
                    Path.Combine(bundleRoot, "firmware.img"),
                    firmwareDevice,
                    "firmware " + plan.TargetBootSlot,
                    cancellationToken);
                if (!await IsBlockDeviceAsync(runner, plan.RecoveryDevice, cancellationToken))
                {
                    throw new InvalidOperationException("recovery block device is missing: " + plan.RecoveryDevice);
                }

                await WriteRawPartitionImageAsync(runner, Path.Combine(bundleRoot, "boot.efi"), plan.BootDevice, "boot " + plan.TargetBootSlot, cancellationToken);
                await WriteCompletePartitionImageAsync(
                    runner,
                    Path.Combine(bundleRoot, "recovery.img"),
                    plan.RecoveryDevice,
                    "recovery " + plan.TargetRecoverySlot,
                    cancellationToken);
                await OtaEspBootAssetInstaller.InstallKernelAssetsAsync(
                    runner,
                    options.Esp,
                    Path.Combine(bundleRoot, "HomeHarborBoot.efi"),
                    plan.BootloaderHash,
                    Path.Combine(bundleRoot, "BOOTX64.EFI"),
                    plan.FallbackBootHash,
                    Path.Combine(bundleRoot, "mmx64.efi"),
                    plan.MokManagerHash,
                    plan.BootMode,
                    dryRun: false,
                    cancellationToken);
                await InstallKernelAddonsToStateAsync(options.StateDir, plan.TargetAddons, bundleRoot, cancellationToken);
                await FileWrites.AtomicWriteTextAsync(options.KernelChannelFile, plan.KernelChannel + Environment.NewLine, 0640, cancellationToken);
            }

            await WriteBootEnvironmentAsync(plan.BootConfig, plan, cancellationToken);
            await EfiBootVariables.SetOneShotAsync(runner, plan.TargetBootSlot, plan.TargetRootSlot, "normal", cancellationToken);
            await WritePendingAsync(options.StateDir, plan, cancellationToken);

            Console.WriteLine($"OTA {plan.Version} staged in boot slot {plan.TargetBootSlot} with root slot {plan.TargetRootSlot}.");
            if (options.Reboot)
            {
                _ = await runner.RunAsync("systemctl", ["reboot"], cancellationToken: cancellationToken);
            }
            else
            {
                Console.WriteLine("--no-reboot set; reboot manually to boot the staged slot.");
            }

            return 0;
        }
        finally
        {
            foreach (var mapName in createdMaps.AsEnumerable().Reverse())
            {
                await new SuperMapper(runner).RemoveAsync(mapName, CancellationToken.None);
            }

            if (rootfsMounted)
            {
                _ = await runner.RunAsync("umount", [Path.Combine(work, "target-rootfs")], cancellationToken: CancellationToken.None);
            }

            TryDeleteDirectory(work);
        }

        async Task<OtaApplyPlan> BuildPlanAsync(
            ICommandRunner commandRunner,
            OtaApplyOptions applyOptions,
            string bundleRoot,
            JsonElement manifest,
            string workDirectory,
            CancellationToken ct)
        {
            var version = RequiredManifestString(manifest, "version");
            var otaType = (ManifestString(manifest, "type") ?? "full-system") switch
            {
                "full-system" => OtaType.FullSystem,
                "kernel-only" => OtaType.KernelOnly,
                var value => throw new InvalidOperationException("OTA manifest type must be full-system or kernel-only, got: " + value)
            };
            var packageKind = ManifestString(manifest, "packageKind") ?? string.Empty;
            if (packageKind is not ("" or "system" or "kernel"))
            {
                throw new InvalidOperationException("OTA manifest packageKind must be system or kernel, got: " + packageKind);
            }

            var channel = RequiredManifestString(manifest, "channel");
            ValidateManifestChannel(channel, applyOptions);
            var releaseSequence = OtaManifestVerifier.ReleaseSequenceProperty(manifest);
            var trustedReleaseAnchors = TrustedCurrentReleaseAnchors(
                applyOptions.CurrentOsRelease,
                applyOptions.CurrentCmdline,
                applyOptions.AllowSequenceBootstrap);
            var trustedReleaseFloor = trustedReleaseAnchors.Floor;

            var bootMode = RequiredManifestString(manifest, "bootMode");
            if (bootMode == "legacy")
            {
                throw new InvalidOperationException("legacy ESP kernel OTA is not supported by the raw UKI partition layout");
            }

            if (bootMode == "secure-boot-uki")
            {
                throw new InvalidOperationException("OTA manifest bootMode=secure-boot-uki has been replaced by secure-boot-raw-uki");
            }

            if (bootMode is not ("raw-uki" or "secure-boot-raw-uki"))
            {
                throw new InvalidOperationException("OTA manifest bootMode must be raw-uki or secure-boot-raw-uki, got: " + bootMode);
            }

            if (packageKind == "system" && otaType != OtaType.FullSystem)
            {
                throw new InvalidOperationException("OTA manifest packageKind=system requires type=full-system");
            }

            if (packageKind == "kernel" && otaType != OtaType.KernelOnly)
            {
                throw new InvalidOperationException("OTA manifest packageKind=kernel requires type=kernel-only");
            }

            if ((packageKind, otaType) is not (("system", OtaType.FullSystem) or ("kernel", OtaType.KernelOnly)))
            {
                throw new InvalidOperationException("raw UKI OTA supports only system/full-system or kernel/kernel-only bundles");
            }
            if (packageKind == "system")
            {
                RequireSystemBundleMatchesRunningKernel(releaseSequence, trustedReleaseAnchors.Kernel);
                ReleaseSequence.RequireComponentUpgrade(
                    releaseSequence,
                    trustedReleaseAnchors.Root,
                    trustedReleaseAnchors.Kernel);
            }
            else
            {
                ReleaseSequence.RequireComponentUpgrade(
                    releaseSequence,
                    trustedReleaseAnchors.Kernel,
                    trustedReleaseAnchors.Root);
            }

            var manifestAddons = ReadManifestAddons(manifest);
            var currentKernelChannel = CurrentKernelChannel(applyOptions);
            var kernelChannel = packageKind == "kernel"
                ? RequireMatchingKernelChannel(
                    KernelChannel.Require(ManifestString(manifest, "kernelChannel"), "OTA manifest kernelChannel"),
                    currentKernelChannel)
                : currentKernelChannel;
            if (packageKind != "kernel" && manifestAddons.Count > 0)
            {
                throw new InvalidOperationException("kernel addons are only valid for kernel OTA manifests");
            }

            var currentBootMode = KernelArg("homeharbor.boot_mode");
            if (!string.IsNullOrWhiteSpace(currentBootMode))
            {
                RequireMatchingBootMode(bootMode, currentBootMode);
            }

            var rootfsPath = Path.Combine(bundleRoot, "rootfs.img");
            var actualRootfsSha = otaType == OtaType.FullSystem
                ? ValidatePayloadHash(bundleRoot, "rootfs.img", "rootfs.img.sha256", manifest, "rootfsHash", "rootfs.img")
                : ManifestString(manifest, "rootfsHash") ?? string.Empty;
            var actualModulesSha = string.Empty;
            var actualFirmwareSha = string.Empty;
            if (otaType == OtaType.KernelOnly)
            {
                actualModulesSha = ValidatePayloadHash(bundleRoot, "modules.img", "modules.img.sha256", manifest, "modulesHash", "modules.img");
                actualFirmwareSha = ValidatePayloadHash(bundleRoot, "firmware.img", "firmware.img.sha256", manifest, "firmwareHash", "firmware.img");
                ValidateAddonPayloadHashes(manifestAddons, bundleRoot);
            }

            var actualRecoverySha = ManifestString(manifest, "recoveryHash") ?? string.Empty;
            if (otaType == OtaType.KernelOnly)
            {
                actualRecoverySha = ValidatePayloadHash(bundleRoot, "recovery.img", "recovery.img.sha256", manifest, "recoveryHash", "recovery.img");
            }
            else if (File.Exists(Path.Combine(bundleRoot, "recovery.img")) || File.Exists(Path.Combine(bundleRoot, "recovery.img.sha256")))
            {
                actualRecoverySha = ValidatePayloadHash(bundleRoot, "recovery.img", "recovery.img.sha256", manifest, "recoveryHash", "recovery.img");
            }

            var actualVbmetaASha = string.Empty;
            var actualVbmetaBSha = string.Empty;
            var actualBootSha = string.Empty;
            var actualBootloaderSha = string.Empty;
            var actualFallbackBootSha = string.Empty;
            var actualMokManagerSha = string.Empty;
            if (otaType == OtaType.FullSystem)
            {
                actualVbmetaASha = ValidatePayloadHash(bundleRoot, "vbmeta_a.img", "vbmeta_a.img.sha256", manifest, "vbmetaAHash", "vbmeta_a.img");
                actualVbmetaBSha = ValidatePayloadHash(bundleRoot, "vbmeta_b.img", "vbmeta_b.img.sha256", manifest, "vbmetaBHash", "vbmeta_b.img");
                actualBootSha = ValidateOptionalPayloadHash(bundleRoot, "boot.efi", "boot.efi.sha256", manifest, "bootHash", "boot.efi");
                actualBootloaderSha = ValidateOptionalPayloadHash(bundleRoot, "HomeHarborBoot.efi", "HomeHarborBoot.efi.sha256", manifest, "bootloaderHash", "HomeHarborBoot.efi");
                actualFallbackBootSha = ValidateOptionalPayloadHash(bundleRoot, "BOOTX64.EFI", "BOOTX64.EFI.sha256", manifest, "fallbackBootHash", "BOOTX64.EFI");
                actualMokManagerSha = ValidateOptionalPayloadHash(bundleRoot, "mmx64.efi", "mmx64.efi.sha256", manifest, "mokManagerHash", "mmx64.efi");
            }
            else
            {
                actualBootSha = ValidatePayloadHash(bundleRoot, "boot.efi", "boot.efi.sha256", manifest, "bootHash", "boot.efi");
                actualBootloaderSha = ValidatePayloadHash(bundleRoot, "HomeHarborBoot.efi", "HomeHarborBoot.efi.sha256", manifest, "bootloaderHash", "HomeHarborBoot.efi");
                actualFallbackBootSha = ValidateOptionalPayloadHash(bundleRoot, "BOOTX64.EFI", "BOOTX64.EFI.sha256", manifest, "fallbackBootHash", "BOOTX64.EFI");
                actualMokManagerSha = ValidateOptionalPayloadHash(bundleRoot, "mmx64.efi", "mmx64.efi.sha256", manifest, "mokManagerHash", "mmx64.efi");
            }

            var activeBootSlot = NormalizeSlot(ActiveBootSlot(applyOptions), "active HomeHarbor boot slot");
            var activeRootSlot = NormalizeSlot(ActiveRootSlot(applyOptions), "active HomeHarbor root slot");
            var targetBootSlot = otaType == OtaType.KernelOnly
                ? OppositeSlot(activeBootSlot)
                : activeBootSlot;

            var targetRootSlot = activeRootSlot;
            var targetRootLower = SlotLower(targetRootSlot);
            var rootLogical = string.Empty;
            var rootDescriptorDigest = string.Empty;
            var rootMapName = string.Empty;
            if (otaType == OtaType.FullSystem)
            {
                targetRootSlot = OppositeSlot(activeRootSlot);
                targetRootLower = SlotLower(targetRootSlot);
                rootLogical = "root_" + targetRootLower;
                rootMapName = "homeharbor-update-root-" + targetRootLower + "-" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
            }
            else
            {
                rootLogical = ActiveEnvValue(applyOptions, activeBootSlot, "HOMEHARBOR_ROOT_LOGICAL") ?? string.Empty;
                rootDescriptorDigest = ActiveEnvValue(applyOptions, activeBootSlot, "HOMEHARBOR_ROOT_DESCRIPTOR_DIGEST") ?? string.Empty;
                if (string.IsNullOrWhiteSpace(rootLogical) || string.IsNullOrWhiteSpace(rootDescriptorDigest))
                {
                    throw new InvalidOperationException("kernel-only OTA could not read the active root boot environment");
                }
            }

            var targetBootLower = SlotLower(targetBootSlot);
            var superDevice = applyOptions.SuperDevice;
            var modulesLogical = "modules_" + targetBootLower;
            var firmwareLogical = "firmware_" + targetBootLower;
            var modulesMapName = "homeharbor-update-modules-" + targetBootLower + "-" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
            var firmwareMapName = "homeharbor-update-firmware-" + targetBootLower + "-" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
            var bootDevice = applyOptions.BootDevice ?? "/dev/disk/by-partlabel/boot_" + targetBootLower;
            var recoveryDevice = applyOptions.RecoveryDevice ?? "/dev/disk/by-partlabel/recovery_" + targetBootLower;
            var vbmetaPartition = "vbmeta_" + targetRootLower;
            var vbmetaDevice = applyOptions.VbmetaDevice ?? "/dev/disk/by-partlabel/" + vbmetaPartition;
            var targetRecoverySlot = otaType == OtaType.KernelOnly ? targetBootSlot : string.Empty;
            var bootConfig = BootEnvForSlot(applyOptions.StateDir, targetBootSlot);
            var entryId = EntryIdForSlot(targetBootSlot);
            var loaderEntry = Path.Combine(applyOptions.Esp, "EFI/HomeHarbor/boot_state.json");
            var kernelRelease = ManifestString(manifest, "kernelRelease")
                ?? ActiveEnvValue(applyOptions, activeBootSlot, "HOMEHARBOR_KERNEL_RELEASE")
                ?? "unknown";
            ValidateKernelRelease(kernelRelease);

            var vbmetaDigest = string.Empty;
            var modulesDescriptorDigest = string.Empty;
            var firmwareDescriptorDigest = string.Empty;
            if (otaType == OtaType.FullSystem)
            {
                vbmetaDigest = targetRootSlot == "A"
                    ? RequiredManifestString(manifest, "vbmetaADigest")
                    : RequiredManifestString(manifest, "vbmetaBDigest");
                var targetVbmeta = Path.Combine(bundleRoot, "vbmeta_" + targetRootLower + ".img");
                var actualVbmetaDigest = await AvbVbmetaDigestAsync(commandRunner, targetVbmeta, ct);
                if (!string.Equals(actualVbmetaDigest, vbmetaDigest, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("target vbmeta digest mismatch");
                }

                rootDescriptorDigest = await AvbDescriptorDigestAsync(commandRunner, targetVbmeta, AvbPartitionNames.DescriptorName(rootLogical), ct);
                modulesDescriptorDigest = ActiveEnvValue(applyOptions, activeBootSlot, "HOMEHARBOR_MODULES_DESCRIPTOR_DIGEST") ?? string.Empty;
                firmwareDescriptorDigest = ActiveEnvValue(applyOptions, activeBootSlot, "HOMEHARBOR_FIRMWARE_DESCRIPTOR_DIGEST") ?? string.Empty;
                if (string.IsNullOrWhiteSpace(modulesDescriptorDigest) || string.IsNullOrWhiteSpace(firmwareDescriptorDigest))
                {
                    throw new InvalidOperationException("system OTA could not read active kernel descriptor digests");
                }
            }
            else
            {
                vbmetaPartition = ActiveEnvValue(applyOptions, activeBootSlot, "HOMEHARBOR_VBMETA_PARTITION") ?? vbmetaPartition;
                vbmetaDigest = ActiveEnvValue(applyOptions, activeBootSlot, "HOMEHARBOR_VBMETA_DIGEST") ?? string.Empty;
                var activeRootVbmeta = Path.Combine(bundleRoot, "vbmeta_" + SlotLower(activeRootSlot) + ".img");
                if (File.Exists(activeRootVbmeta))
                {
                    var expectedRootDescriptor = await AvbDescriptorDigestAsync(commandRunner, activeRootVbmeta, AvbPartitionNames.DescriptorName(rootLogical), ct);
                    if (!string.Equals(expectedRootDescriptor, rootDescriptorDigest, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException("raw UKI kernel-only OTA root descriptor does not match the active root");
                    }
                }

                modulesDescriptorDigest = VerityRootDigestFromArg(Path.Combine(bundleRoot, "modules_" + targetBootLower + ".verity"));
                firmwareDescriptorDigest = VerityRootDigestFromArg(Path.Combine(bundleRoot, "firmware_" + targetBootLower + ".verity"));
            }

            var targetAddons = otaType == OtaType.KernelOnly
                ? manifestAddons
                : ReadActiveAddons(applyOptions, activeBootSlot);
            var currentDataUnlockMode = CurrentDataUnlockMode(applyOptions);
            var targetDataUnlockMode = currentDataUnlockMode;
            if (otaType == OtaType.FullSystem && (!applyOptions.DryRun || !string.IsNullOrWhiteSpace(applyOptions.TargetCrypttab)))
            {
                targetDataUnlockMode = await TargetDataUnlockModeAsync(
                    applyOptions,
                    rootfsPath,
                    workDirectory,
                    currentDataUnlockMode,
                    ct);
                GuardDataUnlockCompatibility(currentDataUnlockMode, targetDataUnlockMode);
            }

            return new OtaApplyPlan(
                Version: version,
                ReleaseSequence: releaseSequence,
                TrustedReleaseFloor: trustedReleaseFloor,
                PackageKind: packageKind,
                Channel: channel,
                KernelChannel: kernelChannel,
                BootMode: bootMode,
                Type: otaType,
                ActiveBootSlot: activeBootSlot,
                TargetBootSlot: targetBootSlot,
                ActiveRootSlot: activeRootSlot,
                TargetRootSlot: targetRootSlot,
                TargetRecoverySlot: targetRecoverySlot,
                SuperDevice: superDevice,
                BootDevice: bootDevice,
                RecoveryDevice: recoveryDevice,
                VbmetaDevice: vbmetaDevice,
                VbmetaPartition: vbmetaPartition,
                VbmetaDigest: vbmetaDigest,
                RootLogical: rootLogical,
                RootDescriptorDigest: rootDescriptorDigest,
                ModulesLogical: modulesLogical,
                ModulesDescriptorDigest: modulesDescriptorDigest,
                FirmwareLogical: firmwareLogical,
                FirmwareDescriptorDigest: firmwareDescriptorDigest,
                RootfsHash: actualRootfsSha,
                ModulesHash: actualModulesSha,
                FirmwareHash: actualFirmwareSha,
                RecoveryHash: actualRecoverySha,
                VbmetaAHash: actualVbmetaASha,
                VbmetaBHash: actualVbmetaBSha,
                BootHash: actualBootSha,
                BootloaderHash: actualBootloaderSha,
                FallbackBootHash: actualFallbackBootSha,
                MokManagerHash: actualMokManagerSha,
                KernelRelease: kernelRelease,
                CurrentDataUnlockMode: currentDataUnlockMode,
                TargetDataUnlockMode: targetDataUnlockMode,
                LoaderEntry: loaderEntry,
                EntryId: entryId,
                BootConfig: bootConfig,
                RootfsPath: rootfsPath,
                RootMapName: rootMapName,
                ModulesMapName: modulesMapName,
                FirmwareMapName: firmwareMapName,
                TargetAddons: targetAddons);
        }

        async Task<string> TargetDataUnlockModeAsync(
            OtaApplyOptions applyOptions,
            string rootfs,
            string workDirectory,
            string currentDataUnlockMode,
            CancellationToken ct)
        {
            if (!string.IsNullOrWhiteSpace(applyOptions.TargetCrypttab))
            {
                return ResolveTargetDataUnlockMode(applyOptions.TargetCrypttab, currentDataUnlockMode);
            }

            var mountDir = Path.Combine(workDirectory, "target-rootfs");
            _ = Directory.CreateDirectory(mountDir);
            await RunRequiredAsync(runner, "mount", ["-o", "loop,ro", rootfs, mountDir], "failed to mount target rootfs", ct);
            rootfsMounted = true;
            var mode = ResolveTargetDataUnlockMode(
                Path.Combine(mountDir, "etc/crypttab"),
                currentDataUnlockMode);
            await RunRequiredAsync(runner, "umount", [mountDir], "failed to unmount target rootfs", ct);
            rootfsMounted = false;
            return mode;
        }
    }

    internal static string CreateWorkDirectory(string workRoot)
    {
        var safeRoot = RootPathGuard.CreateDirectory(workRoot, "OTA work root");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                safeRoot,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var work = RootPathGuard.RequireChildPath(
            Path.Combine(safeRoot, "apply-" + Guid.NewGuid().ToString("N")),
            safeRoot,
            "OTA work directory");
        _ = Directory.CreateDirectory(work);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                work,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return RootPathGuard.RequireChildPath(work, safeRoot, "OTA work directory");
    }

    private static async Task VerifyManifestAsync(
        ICommandRunner runner,
        string manifestPath,
        OtaApplyOptions options,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(options.VerifyScript))
        {
            var result = await runner.RunAsync(options.VerifyScript, [manifestPath, options.PublicKey], cancellationToken: cancellationToken);
            _ = result.EnsureSuccess("manifest signature verification failed");
            return;
        }

        await new OtaManifestVerifier(runner).VerifyAsync(manifestPath, options.PublicKey, cancellationToken);
    }

    private static bool ValidBundleTop(string top)
        => top.StartsWith("homeharbor-system-ota-", StringComparison.Ordinal) ||
           top.StartsWith("homeharbor-kernel-ota-", StringComparison.Ordinal) ||
           (top.StartsWith("homeharbor-kernel-", StringComparison.Ordinal) && top.Contains("-ota-", StringComparison.Ordinal));

    private static async Task<VerifiedBundleManifest> ExtractManifestForVerificationAsync(
        string bundle,
        string work,
        CancellationToken cancellationToken)
    {
        var destination = Path.Combine(work, "manifest-preflight");
        var destinationRoot = FullDirectoryRoot(destination);
        _ = Directory.CreateDirectory(destinationRoot);

        long scannedBytes = 0;
        var memberCount = 0;
        await using var input = File.OpenRead(bundle);
        await using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var reader = new TarReader(gzip);
        TarEntry? entry;
        while ((entry = await reader.GetNextEntryAsync(copyData: false, cancellationToken)) is not null)
        {
            TarSafety.ValidateMemberPath(entry.Name, "OTA bundle");
            memberCount++;
            if (memberCount > MaxOtaBundleMembers)
            {
                throw new InvalidOperationException($"OTA bundle has too many members; maximum is {MaxOtaBundleMembers}");
            }

            if (IsRegularFile(entry.EntryType))
            {
                scannedBytes = AddLimitedSize(
                    scannedBytes,
                    entry.Length,
                    MaxUnsignedManifestScanBytes,
                    "unsigned OTA bundle preflight data",
                    entry.Name);
            }

            if (!IsBundleManifestEntry(entry.Name))
            {
                continue;
            }

            if (!IsRegularFile(entry.EntryType))
            {
                throw new InvalidOperationException("OTA bundle manifest.json must be a regular file");
            }

            if (entry.Length > MaxOtaManifestBytes)
            {
                throw new InvalidOperationException(
                    $"OTA bundle manifest.json exceeds maximum size {MaxOtaManifestBytes}: {entry.Length}");
            }

            var top = TopLevelDirectory(entry.Name);
            if (!ValidBundleTop(top))
            {
                throw new InvalidOperationException("OTA bundle top-level directory must be homeharbor-system-ota-* or homeharbor-kernel-*-ota-*");
            }

            var path = CheckedDestinationPath(destination, entry.Name, destinationRoot);
            _ = Directory.CreateDirectory(Path.GetDirectoryName(path) ?? destination);
            await using (var output = File.Create(path))
            {
                await CopyEntryDataAsync(entry, output, MaxOtaManifestBytes, cancellationToken);
            }

            return new VerifiedBundleManifest(top, path, Sha256File(path));
        }

        throw new InvalidOperationException("OTA bundle does not contain manifest.json");
    }

    private static async Task<string> ExtractBundleAsync(string bundle, string destination, CancellationToken cancellationToken)
    {
        var destinationRoot = FullDirectoryRoot(destination);
        string? top = null;
        long totalBytes = 0;
        var memberCount = 0;
        var extractedMembers = new HashSet<string>(StringComparer.Ordinal);
        await using var input = File.OpenRead(bundle);
        await using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var reader = new TarReader(gzip);
        TarEntry? entry;
        while ((entry = await reader.GetNextEntryAsync(copyData: false, cancellationToken)) is not null)
        {
            TarSafety.ValidateMemberPath(entry.Name, "OTA bundle");
            memberCount++;
            if (memberCount > MaxOtaBundleMembers)
            {
                throw new InvalidOperationException($"OTA bundle has too many members; maximum is {MaxOtaBundleMembers}");
            }

            var memberTop = TopLevelDirectory(entry.Name);
            top ??= memberTop;
            if (!string.Equals(top, memberTop, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("OTA bundle must contain exactly one top-level directory.");
            }

            if (!ValidBundleTop(top))
            {
                throw new InvalidOperationException("OTA bundle top-level directory must be homeharbor-system-ota-* or homeharbor-kernel-*-ota-*");
            }

            var normalizedName = entry.Name.TrimEnd('/');
            if (!extractedMembers.Add(normalizedName))
            {
                throw new InvalidOperationException("duplicate OTA bundle member path: " + entry.Name);
            }

            var path = CheckedDestinationPath(destination, entry.Name, destinationRoot);
            switch (entry.EntryType)
            {
                case TarEntryType.Directory:
                    _ = Directory.CreateDirectory(path);
                    break;
                case TarEntryType.RegularFile:
                case TarEntryType.V7RegularFile:
                case TarEntryType.ContiguousFile:
                    totalBytes = AddLimitedSize(totalBytes, entry.Length, MaxOtaBundleTotalBytes, "OTA bundle decompressed data", entry.Name);
                    if (entry.Length > MaxOtaBundleFileBytes)
                    {
                        throw new InvalidOperationException(
                            $"OTA bundle member exceeds maximum size {MaxOtaBundleFileBytes}: {entry.Name} ({entry.Length})");
                    }

                    _ = Directory.CreateDirectory(Path.GetDirectoryName(path) ?? destination);
                    await using (var output = File.Create(path))
                    {
                        await CopyEntryDataAsync(entry, output, MaxOtaBundleFileBytes, cancellationToken);
                    }

                    break;
                default:
                    throw new InvalidOperationException("unsupported OTA bundle member type: " + entry.Name);
            }
        }

        return top ?? throw new InvalidOperationException("OTA bundle is empty.");
    }

    private static string FullDirectoryRoot(string path)
    {
        var root = Path.GetFullPath(path);
        return root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
    }

    private static string CheckedDestinationPath(string destination, string member, string destinationRoot)
    {
        var path = Path.GetFullPath(Path.Combine(destination, member));
        return path.StartsWith(destinationRoot, StringComparison.Ordinal)
            ? path
            : throw new InvalidOperationException("unsafe OTA bundle member path: " + member);
    }

    private static bool IsBundleManifestEntry(string member)
    {
        var normalized = member.TrimEnd('/');
        var separator = normalized.IndexOf('/');
        return separator > 0 &&
            separator == normalized.LastIndexOf('/') &&
            normalized[(separator + 1)..] == "manifest.json";
    }

    private static string TopLevelDirectory(string member)
    {
        var normalized = member.TrimEnd('/');
        var separator = normalized.IndexOf('/');
        return separator < 0 ? normalized : normalized[..separator];
    }

    private static bool IsRegularFile(TarEntryType entryType)
        => entryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.ContiguousFile;

    private static long AddLimitedSize(long current, long size, long limit, string label, string member)
    {
        if (size < 0)
        {
            throw new InvalidOperationException("OTA bundle member has invalid size: " + member);
        }

        return size > limit || current > limit - size
            ? throw new InvalidOperationException($"{label} exceeds maximum size {limit}: {member}")
            : current + size;
    }

    private static async Task CopyEntryDataAsync(
        TarEntry entry,
        Stream output,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        if (entry.DataStream is null)
        {
            if (entry.Length == 0)
            {
                return;
            }

            throw new InvalidOperationException("OTA bundle member has no data stream: " + entry.Name);
        }

        var buffer = new byte[1024 * 1024];
        long copied = 0;
        int read;
        while ((read = await entry.DataStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
        {
            if (copied > maxBytes - read)
            {
                throw new InvalidOperationException(
                    $"OTA bundle member exceeds maximum size {maxBytes}: {entry.Name}");
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            copied += read;
        }

        if (copied != entry.Length)
        {
            throw new InvalidOperationException(
                $"OTA bundle member size mismatch: {entry.Name} expected {entry.Length}, copied {copied}");
        }
    }

    private static void ValidateManifestChannel(string manifestChannel, OtaApplyOptions options)
    {
        var normalizedManifest = ReleaseChannel.Require(manifestChannel, "OTA manifest channel");
        var currentChannel = CurrentOtaChannel(options);
        if (!string.Equals(normalizedManifest, currentChannel, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"OTA manifest channel {normalizedManifest} does not match current channel {currentChannel}");
        }
    }

    private static string CurrentOtaChannel(OtaApplyOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.Channel))
        {
            return ReleaseChannel.Require(options.Channel, "current OTA channel");
        }

        var fromFile = FirstFileToken(options.ChannelFile);
        return !string.IsNullOrWhiteSpace(fromFile)
            ? ReleaseChannel.Require(fromFile, "current OTA channel")
            : throw new InvalidOperationException("current OTA channel file was not found; pass --channel for recovery use");
    }

    private static string CurrentKernelChannel(OtaApplyOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.KernelChannel))
        {
            return KernelChannel.Require(options.KernelChannel, "current kernel channel");
        }

        var fromFile = FirstFileToken(options.KernelChannelFile);
        return string.IsNullOrWhiteSpace(fromFile)
            ? KernelChannel.Generic
            : KernelChannel.Require(fromFile, "current kernel channel");
    }

    internal static string RequireMatchingKernelChannel(string manifestChannel, string currentChannel)
    {
        var manifest = KernelChannel.Require(manifestChannel, "OTA manifest kernelChannel");
        var current = KernelChannel.Require(currentChannel, "current kernel channel");
        return string.Equals(manifest, current, StringComparison.Ordinal)
            ? manifest
            : throw new InvalidOperationException($"OTA kernel channel {manifest} does not match current kernel channel {current}");
    }

    internal static void RequireMatchingBootMode(string manifestBootMode, string currentBootMode)
    {
        if (currentBootMode is not ("raw-uki" or "secure-boot-raw-uki"))
        {
            throw new InvalidOperationException("current boot mode is unsupported: " + currentBootMode);
        }

        if (!string.Equals(manifestBootMode, currentBootMode, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"OTA boot mode {manifestBootMode} does not match current boot mode {currentBootMode}");
        }
    }

    private static string? FirstFileToken(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        foreach (var line in File.ReadLines(path))
        {
            var token = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(token))
            {
                return token;
            }
        }

        return null;
    }

    private static string ValidatePayloadHash(
        string bundleRoot,
        string fileName,
        string shaFileName,
        JsonElement manifest,
        string manifestKey,
        string label)
    {
        var path = Path.Combine(bundleRoot, fileName);
        var shaPath = Path.Combine(bundleRoot, shaFileName);
        if (!File.Exists(path))
        {
            throw new InvalidOperationException("OTA bundle must contain " + label);
        }

        if (!File.Exists(shaPath))
        {
            throw new InvalidOperationException("OTA bundle must contain " + label + ".sha256");
        }

        var expected = FirstFileToken(shaPath) ?? string.Empty;
        var actual = Sha256File(path);
        var manifestValue = ManifestString(manifest, manifestKey) ?? string.Empty;
        return !string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(manifestValue, actual, StringComparison.OrdinalIgnoreCase)
            ? throw new InvalidOperationException(
                "OTA " + label + " hash mismatch" + Environment.NewLine +
                label + ".sha256: " + expected + Environment.NewLine +
                "manifest:        " + manifestValue + Environment.NewLine +
                "actual:          " + actual)
            : actual;
    }

    private static string ValidateOptionalPayloadHash(
        string bundleRoot,
        string fileName,
        string shaFileName,
        JsonElement manifest,
        string manifestKey,
        string label)
    {
        if (!manifest.TryGetProperty(manifestKey, out var property) ||
            property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return string.Empty;
        }

        var manifestValue = ManifestString(manifest, manifestKey);
        return string.IsNullOrWhiteSpace(manifestValue)
            ? string.Empty
            : ValidatePayloadHash(bundleRoot, fileName, shaFileName, manifest, manifestKey, label);
    }

    private static string Sha256File(string path)
    {
        using var input = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
    }

    private static IReadOnlyList<OtaAddon> ReadManifestAddons(JsonElement manifest)
    {
        if (!manifest.TryGetProperty("addons", out var addons) ||
            addons.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return [];
        }

        if (addons.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("OTA manifest addons must be an array");
        }

        var result = new List<OtaAddon>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var suffixes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var addon in addons.EnumerateArray())
        {
            var key = RequiredManifestString(addon, "key");
            ValidateAddonKey(key);
            if (!keys.Add(key))
            {
                throw new InvalidOperationException("duplicate kernel addon key: " + key);
            }

            if (!suffixes.Add(AddonEnvSuffix(key)))
            {
                throw new InvalidOperationException("kernel addon key has a boot env suffix collision: " + key);
            }

            var file = RequiredManifestString(addon, "file");
            TarSafety.ValidateMemberPath(file, "OTA manifest addon file");
            if (!file.StartsWith("addons/", StringComparison.Ordinal) || !file.EndsWith(".erofs", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("kernel addon file must be under addons/ and end with .erofs: " + file);
            }

            var sha = RequiredManifestString(addon, "sha256");
            ValidateSha256Hex(sha, "kernel addon " + key + " sha256");
            if (RequiredManifestString(addon, "filesystem") != "erofs")
            {
                throw new InvalidOperationException("kernel addon " + key + " filesystem must be erofs");
            }

            if (RequiredManifestString(addon, "overlay") != "usr")
            {
                throw new InvalidOperationException("kernel addon " + key + " overlay must be usr");
            }

            result.Add(new OtaAddon(key, file, sha.ToLowerInvariant()));
        }

        return result;
    }

    private static void ValidateAddonPayloadHashes(IReadOnlyList<OtaAddon> addons, string bundleRoot)
    {
        foreach (var addon in addons)
        {
            var path = Path.Combine(bundleRoot, addon.File);
            if (!File.Exists(path))
            {
                throw new InvalidOperationException("kernel OTA missing addon image: " + addon.File);
            }

            var actual = Sha256File(path);
            if (!string.Equals(actual, addon.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("kernel addon " + addon.Key + " hash mismatch");
            }
        }
    }

    private static IReadOnlyList<OtaAddon> ReadActiveAddons(OtaApplyOptions options, string activeBootSlot)
    {
        var list = ActiveEnvValue(options, activeBootSlot, "HOMEHARBOR_ADDONS");
        if (string.IsNullOrWhiteSpace(list))
        {
            return [];
        }

        var result = new List<OtaAddon>();
        foreach (var key in list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            ValidateAddonKey(key);
            var sha = ActiveEnvValue(options, activeBootSlot, "HOMEHARBOR_ADDON_" + AddonEnvSuffix(key) + "_SHA256") ?? string.Empty;
            ValidateSha256Hex(sha, "active kernel addon " + key + " sha256");
            result.Add(new OtaAddon(key, string.Empty, sha.ToLowerInvariant()));
        }

        return result;
    }

    private static async Task InstallKernelAddonsToStateAsync(
        string stateDir,
        IReadOnlyList<OtaAddon> addons,
        string bundleRoot,
        CancellationToken cancellationToken)
    {
        if (addons.Count == 0)
        {
            return;
        }

        var store = Path.Combine(stateDir, "addons/store");
        _ = Directory.CreateDirectory(store);
        foreach (var addon in addons)
        {
            var source = Path.Combine(bundleRoot, addon.File);
            var destination = Path.Combine(store, addon.Sha256 + ".erofs");
            if (File.Exists(destination))
            {
                var actual = Sha256File(destination);
                if (!string.Equals(actual, addon.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("state addon store entry has unexpected content: " + destination);
                }

                continue;
            }

            await FileWrites.CopyFileAsync(source, destination, 0640, cancellationToken);
            var copied = Sha256File(destination);
            if (!string.Equals(copied, addon.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("copied kernel addon " + addon.Key + " hash mismatch");
            }
        }
    }

    private static void ValidateAddonKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Any(c => !(char.IsAsciiLetterLower(c) || char.IsDigit(c) || c is '.' or '_' or '-')))
        {
            throw new InvalidOperationException("kernel addon key is invalid: " + key);
        }
    }

    private static void ValidateSha256Hex(string value, string label)
    {
        if (value.Length != 64 || !value.All(Uri.IsHexDigit))
        {
            throw new InvalidOperationException(label + " must be a SHA-256 hex digest");
        }
    }

    private static string AddonEnvSuffix(string key)
    {
        var builder = new StringBuilder(key.Length);
        foreach (var c in key)
        {
            _ = builder.Append(c is '.' or '-' ? '_' : char.ToUpperInvariant(c));
        }

        return builder.ToString();
    }

    private static string AddonList(IReadOnlyList<OtaAddon> addons)
        => string.Join(',', addons.Select(addon => addon.Key));

    private static string VerityRootDigestFromArg(string path)
    {
        if (!File.Exists(path))
        {
            throw new InvalidOperationException("kernel OTA missing verity sidecar: " + Path.GetFileName(path));
        }

        var arg = FirstFileToken(path) ?? string.Empty;
        var parts = arg.Split(':');
        var digest = parts.ElementAtOrDefault(6) ?? string.Empty;
        return digest.Length == 0 || !digest.All(Uri.IsHexDigit)
            ? throw new InvalidOperationException("invalid verity root digest in " + Path.GetFileName(path))
            : digest;
    }

    private static async Task<string> AvbVbmetaDigestAsync(ICommandRunner runner, string image, CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync("avbtool", ["calculate_vbmeta_digest", "--image", image], cancellationToken: cancellationToken);
        _ = result.EnsureSuccess("avbtool calculate_vbmeta_digest failed");
        var digest = result.Stdout.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return string.IsNullOrWhiteSpace(digest)
            ? throw new InvalidOperationException("avbtool did not report a vbmeta digest for " + image)
            : digest;
    }

    private static async Task<string> AvbDescriptorDigestAsync(ICommandRunner runner, string image, string partitionName, CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync("avbtool", ["info_image", "--image", image], cancellationToken: cancellationToken);
        _ = result.EnsureSuccess("avbtool info_image failed");
        var part = string.Empty;
        var digest = string.Empty;
        var found = string.Empty;
        foreach (var rawLine in result.Stdout.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line == "Hashtree descriptor:")
            {
                if (part == partitionName && digest.Length > 0)
                {
                    found = digest;
                }

                part = string.Empty;
                digest = string.Empty;
                continue;
            }

            if (line.StartsWith("Partition Name:", StringComparison.Ordinal))
            {
                part = line["Partition Name:".Length..].Trim();
            }
            else if (line.StartsWith("Root Digest:", StringComparison.Ordinal))
            {
                digest = line["Root Digest:".Length..].Trim();
            }
        }

        if (part == partitionName && digest.Length > 0)
        {
            found = digest;
        }

        return string.IsNullOrWhiteSpace(found)
            ? throw new InvalidOperationException("avbtool did not report a descriptor digest for " + partitionName)
            : found;
    }

    private static string ActiveBootSlot(OtaApplyOptions options)
    {
        foreach (var slot in new[]
                 {
                     options.ActiveBootSlot,
                     BootEnvValue(options.BootEnv, "HOMEHARBOR_BOOT_SLOT"),
                     KernelArg("homeharbor.boot_slot"),
                     options.ActiveSlot,
                     BootEnvValue(options.BootEnv, "HOMEHARBOR_SLOT")
                 })
        {
            if (!string.IsNullOrWhiteSpace(slot))
            {
                return slot;
            }
        }

        throw new InvalidOperationException("cannot determine active HomeHarbor boot slot; pass --active-boot-slot for recovery use");
    }

    private static string ActiveRootSlot(OtaApplyOptions options)
    {
        foreach (var slot in new[]
                 {
                     options.ActiveRootSlot,
                     options.ActiveSlot,
                     BootEnvValue(options.BootEnv, "HOMEHARBOR_SLOT"),
                     KernelArg("homeharbor.slot")
                 })
        {
            if (!string.IsNullOrWhiteSpace(slot))
            {
                return slot;
            }
        }

        throw new InvalidOperationException("cannot determine active HomeHarbor root slot; pass --active-root-slot for recovery use");
    }

    private static string? ActiveEnvValue(OtaApplyOptions options, string activeBootSlot, string key)
    {
        var value = BootEnvValue(options.BootEnv, key);
        if (!string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        if (!string.IsNullOrWhiteSpace(activeBootSlot))
        {
            value = BootEnvValue(BootEnvForSlot(options.StateDir, activeBootSlot), key);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        value = BootEnvValue(Path.Combine(options.StateDir, "current.env"), key);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string? BootEnvValue(string path, string key)
    {
        var values = BootEnvironment.Read(path);
        return values.TryGetValue(key, out var value) ? value : null;
    }

    private static string? KernelArg(string key)
    {
        const string cmdline = "/proc/cmdline";
        if (!File.Exists(cmdline))
        {
            return null;
        }

        foreach (var token in File.ReadAllText(cmdline).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = token.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            if (token[..separator] == key)
            {
                return token[(separator + 1)..];
            }
        }

        return null;
    }

    private static string NormalizeSlot(string slot, string label)
    {
        var normalized = slot.Trim().ToUpperInvariant();
        return normalized is "A" or "B"
            ? normalized
            : throw new InvalidOperationException(label + " must be A or B, got: " + slot);
    }

    private static string OppositeSlot(string slot)
        => slot switch
        {
            "A" => "B",
            "B" => "A",
            _ => throw new InvalidOperationException("slot must be A or B, got: " + slot)
        };

    private static string SlotLower(string slot)
        => slot.ToLowerInvariant();

    private static string BootEnvForSlot(string stateDir, string slot)
        => Path.Combine(stateDir, "boot_" + SlotLower(slot) + ".env");

    private static string EntryIdForSlot(string slot)
        => "homeharbor_" + SlotLower(slot) + ".conf";

    private static void ValidateKernelRelease(string kernelRelease)
    {
        if (string.IsNullOrWhiteSpace(kernelRelease) ||
            kernelRelease.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '+' or '-')))
        {
            throw new InvalidOperationException("OTA kernelRelease contains unsafe characters: " + kernelRelease);
        }
    }

    private static string CurrentDataUnlockMode(OtaApplyOptions options)
    {
        var metadata = MetadataDataUnlockMode(options.DataUnlockMetadata);
        if (!string.IsNullOrWhiteSpace(metadata))
        {
            return metadata;
        }

        var crypttab = CrypttabDataUnlockMode(options.CurrentCrypttab);
        return string.IsNullOrWhiteSpace(crypttab) ? "passphrase" : crypttab;
    }

    private static string? MetadataDataUnlockMode(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var mode = ManifestString(doc.RootElement, "unlockMode");
            return mode switch
            {
                null or "" => null,
                "passphrase" or "tpm2" => mode,
                _ => "unsupported-metadata"
            };
        }
        catch (JsonException)
        {
            return "unsupported-metadata";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? CrypttabDataUnlockMode(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        IEnumerable<string> lines;
        try
        {
            lines = File.ReadLines(path).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return CrypttabDataUnlockMode(lines);
    }

    internal static string ResolveTargetDataUnlockMode(string targetCrypttab, string currentDataUnlockMode)
    {
        if (!File.Exists(targetCrypttab))
        {
            throw new InvalidOperationException("target rootfs crypttab is missing: " + targetCrypttab);
        }

        string[] lines;
        try
        {
            lines = File.ReadAllLines(targetCrypttab);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException("could not read target rootfs crypttab: " + targetCrypttab, ex);
        }

        var activeLines = lines.Where(IsActiveCrypttabLine).ToArray();
        if (activeLines.Length > 0)
        {
            var configuredMode = activeLines.Length == 1
                ? CrypttabDataUnlockMode(activeLines)
                : null;
            return configuredMode
                ?? throw new InvalidOperationException(
                    "target rootfs crypttab must be empty or contain exactly one valid homeharbor-data entry: " + targetCrypttab);
        }

        if (currentDataUnlockMode is not ("passphrase" or "tpm2"))
        {
            throw new InvalidOperationException("current data unlock mode is unsupported: " + currentDataUnlockMode);
        }

        // Rootfs images intentionally ship an empty crypttab. Data-device identity and
        // unlock policy live on the persistent state partition, so an empty target file
        // inherits the already-validated current mode instead of inventing new policy.
        return currentDataUnlockMode;
    }

    private static string? CrypttabDataUnlockMode(IEnumerable<string> lines)
    {
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (!IsActiveCrypttabLine(line))
            {
                continue;
            }

            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3 || parts[0] != "homeharbor-data")
            {
                continue;
            }

            return parts[2] != "none"
                ? "unsupported-keyfile"
                : parts.ElementAtOrDefault(3)?.Contains("tpm2-device=", StringComparison.Ordinal) == true
                ? "tpm2"
                : "passphrase";
        }

        return null;
    }

    private static bool IsActiveCrypttabLine(string line)
    {
        var trimmed = line.Trim();
        return trimmed.Length > 0 && !trimmed.StartsWith('#');
    }

    private static void GuardDataUnlockCompatibility(string current, string target)
    {
        if (current is not ("passphrase" or "tpm2"))
        {
            throw new InvalidOperationException("current data unlock mode is unsupported: " + current);
        }

        if (target is not ("passphrase" or "tpm2"))
        {
            throw new InvalidOperationException("target data unlock mode is unsupported: " + target);
        }
    }

    private static void WriteDryRun(OtaApplyPlan plan)
    {
        var builder = new StringBuilder();
        _ = builder.AppendLine("OTA bundle verified.");
        _ = builder.AppendLine("version=" + plan.Version);
        _ = builder.AppendLine("releaseSequence=" + plan.ReleaseSequence.ToString(CultureInfo.InvariantCulture));
        _ = builder.AppendLine("trustedReleaseFloor=" + plan.TrustedReleaseFloor.ToString(CultureInfo.InvariantCulture));
        _ = builder.AppendLine("packageKind=" + plan.PackageKind);
        _ = builder.AppendLine("channel=" + plan.Channel);
        _ = builder.AppendLine("kernelChannel=" + plan.KernelChannel);
        _ = builder.AppendLine("bootMode=" + plan.BootMode);
        _ = builder.AppendLine("type=" + TypeText(plan.Type));
        _ = builder.AppendLine("activeBootSlot=" + plan.ActiveBootSlot);
        _ = builder.AppendLine("targetBootSlot=" + plan.TargetBootSlot);
        _ = builder.AppendLine("activeRootSlot=" + plan.ActiveRootSlot);
        _ = builder.AppendLine("targetRootSlot=" + plan.TargetRootSlot);
        _ = builder.AppendLine("targetRecoverySlot=" + plan.TargetRecoverySlot);
        _ = builder.AppendLine("superDevice=" + plan.SuperDevice);
        _ = builder.AppendLine("bootDevice=" + plan.BootDevice);
        _ = builder.AppendLine("recoveryDevice=" + plan.RecoveryDevice);
        _ = builder.AppendLine("vbmetaDevice=" + plan.VbmetaDevice);
        _ = builder.AppendLine("vbmetaPartition=" + plan.VbmetaPartition);
        _ = builder.AppendLine("vbmetaDigest=" + plan.VbmetaDigest);
        _ = builder.AppendLine("rootLogical=" + plan.RootLogical);
        _ = builder.AppendLine("rootDescriptorDigest=" + plan.RootDescriptorDigest);
        _ = builder.AppendLine("modulesLogical=" + plan.ModulesLogical);
        _ = builder.AppendLine("modulesDescriptorDigest=" + plan.ModulesDescriptorDigest);
        _ = builder.AppendLine("firmwareLogical=" + plan.FirmwareLogical);
        _ = builder.AppendLine("firmwareDescriptorDigest=" + plan.FirmwareDescriptorDigest);
        _ = builder.AppendLine("rootfsHash=" + plan.RootfsHash);
        _ = builder.AppendLine("modulesHash=" + plan.ModulesHash);
        _ = builder.AppendLine("firmwareHash=" + plan.FirmwareHash);
        _ = builder.AppendLine("recoveryHash=" + plan.RecoveryHash);
        _ = builder.AppendLine("vbmetaAHash=" + plan.VbmetaAHash);
        _ = builder.AppendLine("vbmetaBHash=" + plan.VbmetaBHash);
        _ = builder.AppendLine("bootHash=" + plan.BootHash);
        _ = builder.AppendLine("bootloaderHash=" + plan.BootloaderHash);
        _ = builder.AppendLine("fallbackBootHash=" + plan.FallbackBootHash);
        _ = builder.AppendLine("mokManagerHash=" + plan.MokManagerHash);
        _ = builder.AppendLine("kernelRelease=" + plan.KernelRelease);
        _ = builder.AppendLine("kernelAddons=" + AddonList(plan.TargetAddons));
        _ = builder.AppendLine("currentDataUnlockMode=" + plan.CurrentDataUnlockMode);
        _ = builder.AppendLine("targetDataUnlockMode=" + plan.TargetDataUnlockMode);
        _ = builder.AppendLine("loaderEntry=" + plan.LoaderEntry);
        _ = builder.AppendLine("entryId=" + plan.EntryId);
        _ = builder.AppendLine("bootConfig=" + plan.BootConfig);
        Console.Write(builder.ToString());
    }

    private static async Task WriteBootEnvironmentAsync(string path, OtaApplyPlan plan, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        _ = builder.AppendLine("HOMEHARBOR_BOOT_SLOT=" + plan.TargetBootSlot);
        _ = builder.AppendLine("HOMEHARBOR_SLOT=" + plan.TargetRootSlot);
        _ = builder.AppendLine("HOMEHARBOR_ROOT_LOGICAL=" + plan.RootLogical);
        _ = builder.AppendLine("HOMEHARBOR_KERNEL_RELEASE=" + plan.KernelRelease);
        _ = builder.AppendLine("HOMEHARBOR_MODULES_LOGICAL=" + plan.ModulesLogical);
        _ = builder.AppendLine("HOMEHARBOR_FIRMWARE_LOGICAL=" + plan.FirmwareLogical);
        _ = builder.AppendLine("HOMEHARBOR_VBMETA_PARTITION=" + plan.VbmetaPartition);
        _ = builder.AppendLine("HOMEHARBOR_VBMETA_DIGEST=" + plan.VbmetaDigest);
        _ = builder.AppendLine("HOMEHARBOR_ROOT_DESCRIPTOR_DIGEST=" + plan.RootDescriptorDigest);
        _ = builder.AppendLine("HOMEHARBOR_MODULES_DESCRIPTOR_DIGEST=" + plan.ModulesDescriptorDigest);
        _ = builder.AppendLine("HOMEHARBOR_FIRMWARE_DESCRIPTOR_DIGEST=" + plan.FirmwareDescriptorDigest);
        _ = builder.AppendLine("HOMEHARBOR_VERSION=" + plan.Version);
        _ = builder.AppendLine(ReleaseSequence.OsReleaseKey + "=" + plan.ReleaseSequence.ToString(CultureInfo.InvariantCulture));
        var addonList = AddonList(plan.TargetAddons);
        if (!string.IsNullOrWhiteSpace(addonList))
        {
            _ = builder.AppendLine("HOMEHARBOR_ADDONS=" + addonList);
            foreach (var addon in plan.TargetAddons)
            {
                _ = builder.AppendLine("HOMEHARBOR_ADDON_" + AddonEnvSuffix(addon.Key) + "_SHA256=" + addon.Sha256);
            }
        }

        await FileWrites.AtomicWriteTextAsync(path, builder.ToString(), 0644, cancellationToken);
    }

    private static async Task WritePendingAsync(string stateDir, OtaApplyPlan plan, CancellationToken cancellationToken)
    {
        var pending = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["bootMode"] = plan.BootMode,
            ["bootConfig"] = plan.BootConfig,
            ["bootHash"] = plan.BootHash,
            ["bootloaderHash"] = plan.BootloaderHash,
            ["entryId"] = plan.EntryId,
            ["firmwareDescriptorDigest"] = plan.FirmwareDescriptorDigest,
            ["firmwareHash"] = plan.FirmwareHash,
            ["firmwareLogical"] = plan.FirmwareLogical,
            ["fromBootSlot"] = plan.ActiveBootSlot,
            ["fromRootSlot"] = plan.ActiveRootSlot,
            ["kernelChannel"] = plan.KernelChannel,
            ["kernelRelease"] = plan.KernelRelease,
            ["modulesDescriptorDigest"] = plan.ModulesDescriptorDigest,
            ["modulesHash"] = plan.ModulesHash,
            ["modulesLogical"] = plan.ModulesLogical,
            ["mokManagerHash"] = plan.MokManagerHash,
            ["recoveryHash"] = plan.RecoveryHash,
            ["releaseSequence"] = plan.ReleaseSequence,
            ["rootDescriptorDigest"] = plan.RootDescriptorDigest,
            ["rootfsHash"] = plan.RootfsHash,
            ["rootLogical"] = plan.RootLogical,
            ["stagedAt"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            ["targetBootSlot"] = plan.TargetBootSlot,
            ["targetRecoverySlot"] = plan.TargetRecoverySlot,
            ["targetRootSlot"] = plan.TargetRootSlot,
            ["type"] = TypeText(plan.Type),
            ["vbmetaAHash"] = plan.VbmetaAHash,
            ["vbmetaBHash"] = plan.VbmetaBHash,
            ["vbmetaDigest"] = plan.VbmetaDigest,
            ["vbmetaPartition"] = plan.VbmetaPartition,
            ["version"] = plan.Version
        };
        if (plan.TargetAddons.Count > 0)
        {
            pending["addons"] = plan.TargetAddons.Select(addon => new
            {
                key = addon.Key,
                sha256 = addon.Sha256,
                filesystem = "erofs",
                overlay = "usr"
            }).ToArray();
        }

        await FileWrites.AtomicWriteTextAsync(Path.Combine(stateDir, "pending.json"), JsonSerializer.Serialize(pending, JsonOptions), 0640, cancellationToken);
    }

    private static async Task WriteRawPartitionImageAsync(
        ICommandRunner runner,
        string image,
        string device,
        string label,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(image))
        {
            throw new InvalidOperationException(label + " image is missing: " + image);
        }

        if (!await IsBlockDeviceAsync(runner, device, cancellationToken))
        {
            throw new InvalidOperationException(label + " block device is missing: " + device);
        }

        var imageSize = new FileInfo(image).Length;
        var sizeResult = await runner.RunAsync("blockdev", ["--getsize64", device], cancellationToken: cancellationToken);
        _ = sizeResult.EnsureSuccess("failed to read raw partition size");
        if (!long.TryParse(sizeResult.Stdout.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var deviceSize))
        {
            throw new InvalidOperationException("raw partition size is not numeric: " + device);
        }

        if (imageSize > deviceSize)
        {
            throw new InvalidOperationException($"{label} image is larger than its raw partition: {imageSize} > {deviceSize}");
        }

        const long zeroBlockSize = 4 * 1024 * 1024;
        var zeroBlocks = deviceSize / zeroBlockSize;
        var zeroRemainder = deviceSize % zeroBlockSize;
        var zeroOffset = zeroBlocks * zeroBlockSize;
        if (zeroBlocks > 0)
        {
            await RunRequiredAsync(runner, "dd", ["if=/dev/zero", "of=" + device, "bs=" + zeroBlockSize.ToString(CultureInfo.InvariantCulture), "count=" + zeroBlocks.ToString(CultureInfo.InvariantCulture), "conv=fsync", "status=none"], "failed to zero raw partition", cancellationToken);
        }

        if (zeroRemainder > 0)
        {
            await RunRequiredAsync(runner, "dd", ["if=/dev/zero", "of=" + device, "bs=1", "count=" + zeroRemainder.ToString(CultureInfo.InvariantCulture), "seek=" + zeroOffset.ToString(CultureInfo.InvariantCulture), "conv=fsync,notrunc", "status=none"], "failed to zero raw partition tail", cancellationToken);
        }

        await RunRequiredAsync(runner, "dd", ["if=" + image, "of=" + device, "bs=4M", "conv=fsync,notrunc", "status=progress"], "failed to write raw partition image", cancellationToken, stream: true);
    }

    private static async Task WriteCompletePartitionImageAsync(
        ICommandRunner runner,
        string image,
        string device,
        string label,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(image))
        {
            throw new InvalidOperationException(label + " complete partition image is missing: " + image);
        }
        if (!await IsBlockDeviceAsync(runner, device, cancellationToken))
        {
            throw new InvalidOperationException(label + " block device is missing: " + device);
        }

        var sizeResult = await runner.RunAsync("blockdev", ["--getsize64", device], cancellationToken: cancellationToken);
        _ = sizeResult.EnsureSuccess("failed to read complete partition size");
        if (!long.TryParse(sizeResult.Stdout.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var deviceSize) ||
            deviceSize <= 0 || new FileInfo(image).Length != deviceSize)
        {
            throw new InvalidOperationException(label + " image size does not exactly match its target partition");
        }

        await RunRequiredAsync(
            runner,
            "dd",
            ["if=" + image, "of=" + device, "bs=4M", "conv=fsync", "status=progress"],
            "failed to write complete " + label + " partition image",
            cancellationToken,
            stream: true);
    }

    private static async Task<bool> IsBlockDeviceAsync(ICommandRunner runner, string path, CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync("test", ["-b", path], cancellationToken: cancellationToken);
        return result.ExitCode == 0;
    }

    private static async Task RunRequiredAsync(
        ICommandRunner runner,
        string fileName,
        IEnumerable<string> arguments,
        string message,
        CancellationToken cancellationToken,
        bool stream = false)
    {
        var result = await runner.RunAsync(
            fileName,
            arguments,
            new CommandRunOptions(StreamOutput: stream, StreamError: stream),
            cancellationToken);
        _ = result.EnsureSuccess(message);
    }

    internal static TrustedReleaseAnchors TrustedCurrentReleaseAnchors(
        string osReleasePath,
        string cmdlinePath,
        bool allowSequenceBootstrap)
    {
        var rootSequence = ReleaseSequence.ReadOptionalOsRelease(osReleasePath);
        var kernelSequence = ReleaseSequence.ReadOptionalKernelCommandLine(cmdlinePath);
        if (!allowSequenceBootstrap && (rootSequence is null || kernelSequence is null))
        {
            throw new InvalidOperationException(
                "current verified rootfs and signed kernel must both carry releaseSequence; " +
                "use --allow-sequence-bootstrap only while transitioning both components from a legacy image");
        }

        return new TrustedReleaseAnchors(rootSequence ?? 0, kernelSequence ?? 0);
    }

    internal static void RequireSystemBundleMatchesRunningKernel(long targetSequence, long runningKernelSequence)
    {
        _ = ReleaseSequence.RequirePositive(targetSequence, "system OTA releaseSequence");
        if (runningKernelSequence < 0)
        {
            throw new InvalidOperationException("running kernel releaseSequence cannot be negative");
        }
        if (targetSequence > runningKernelSequence)
        {
            throw new InvalidOperationException(
                $"system OTA releaseSequence {targetSequence} is newer than the running kernel sequence {runningKernelSequence}; " +
                "apply and boot the matching kernel bundle first");
        }
        if (targetSequence != runningKernelSequence)
        {
            throw new InvalidOperationException(
                $"system OTA releaseSequence {targetSequence} must exactly match the running kernel sequence {runningKernelSequence}");
        }
    }

    private static string RequiredManifestString(JsonElement element, string name)
        => ManifestString(element, name)
           ?? throw new InvalidOperationException("manifest is missing required field: " + name);

    private static string? ManifestString(JsonElement element, string name)
        => element.TryGetProperty(name, out var property) && property.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
            ? property.ValueKind == JsonValueKind.String ? property.GetString() : property.ToString()
            : null;

    private static string TypeText(OtaType type)
        => type == OtaType.FullSystem ? "full-system" : "kernel-only";

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
        }
    }

    private sealed record VerifiedBundleManifest(string TopDirectory, string Path, string Sha256);

    internal sealed record TrustedReleaseAnchors(long Root, long Kernel)
    {
        public long Floor => Math.Max(Root, Kernel);
    }

    private sealed record OtaApplyOptions(
        string Bundle,
        string PublicKey,
        string StateDir,
        string WorkDirectory,
        string ChannelFile,
        string? Channel,
        string KernelChannelFile,
        string? KernelChannel,
        string Esp,
        string BootEnv,
        string CurrentOsRelease,
        string CurrentCmdline,
        bool AllowSequenceBootstrap,
        bool DryRun,
        bool Reboot,
        string DataUnlockMetadata,
        string CurrentCrypttab,
        string? TargetCrypttab,
        string? ActiveSlot,
        string? ActiveBootSlot,
        string? ActiveRootSlot,
        string SuperDevice,
        string? BootDevice,
        string? RecoveryDevice,
        string? VbmetaDevice,
        string? VerifyScript)
    {
        public static OtaApplyOptions Parse(string[] args)
        {
            var commandOptions = CreateCommandOptions();
            var command = new Command("ota-apply");
            commandOptions.AddTo(command);
            var parseResult = command.Parse(args);
            return parseResult.Errors.Count > 0
                ? throw new ArgumentException(string.Join(Environment.NewLine, parseResult.Errors.Select(error => error.Message)))
                : FromParseResult(parseResult, commandOptions);
        }

        public static CommandOptions CreateCommandOptions()
            => new(
                new Argument<string>("bundle") { Description = "HomeHarbor OTA bundle." },
                StringOption("--public-key", "/etc/homeharbor/release.pub.pem", "Release public key."),
                StringOption("--state-dir", "/var/lib/homeharbor/ota", "OTA state directory."),
                StringOption("--work-dir", "/homeharbor-data/.homeharbor-ota-work", "Root-owned OTA extraction work directory."),
                NullableStringOption("--channel-file", "OTA channel file."),
                NullableStringOption("--channel", "Expected OTA release channel."),
                NullableStringOption("--kernel-channel-file", "Kernel channel file."),
                NullableStringOption("--kernel-channel", "Expected kernel channel."),
                StringOption("--esp", "/efi", "EFI system partition mount path."),
                StringOption("--boot-env", "/run/homeharbor-boot/boot.env", "Current boot environment file."),
                StringOption("--current-os-release", "/usr/lib/os-release", "Verified current rootfs os-release path."),
                StringOption("--current-cmdline", "/proc/cmdline", "Signed current kernel command line path."),
                new Option<bool>("--allow-sequence-bootstrap") { Description = "Allow a controlled transition from a legacy image missing releaseSequence anchors." },
                new Option<bool>("--dry-run") { Description = "Print the OTA plan without applying it." },
                new Option<bool>("--reboot") { Description = "Reboot after applying the OTA." },
                new Option<bool>("--no-reboot") { Description = "Do not reboot after applying the OTA." },
                StringOption("--data-unlock-metadata", "/var/lib/homeharbor/security/data-unlock.json", "Data unlock metadata path."),
                StringOption("--current-crypttab", "/etc/crypttab", "Current crypttab path."),
                NullableStringOption("--target-crypttab", "Target crypttab path."),
                NullableStringOption("--active-slot", "Override active slot."),
                NullableStringOption("--active-boot-slot", "Override active boot slot."),
                NullableStringOption("--active-root-slot", "Override active root slot."),
                StringOption("--super-device", "/dev/disk/by-partlabel/super", "Super partition device path."),
                NullableStringOption("--boot-device", "Boot partition device path."),
                NullableStringOption("--recovery-device", "Recovery partition device path."),
                NullableStringOption("--vbmeta-device", "VBMeta partition device path."),
                NullableStringOption("--verify-script", "Manifest verification helper script."));

        public static OtaApplyOptions FromParseResult(ParseResult parseResult, CommandOptions options)
        {
            var stateDir = parseResult.GetValue(options.StateDir)!;
            var channelFile = parseResult.GetValue(options.ChannelFile);
            var kernelChannelFile = parseResult.GetValue(options.KernelChannelFile);
            channelFile ??= Path.Combine(stateDir, "channel");
            kernelChannelFile ??= Path.Combine(stateDir, "kernel-channel");
            return new OtaApplyOptions(
                parseResult.GetValue(options.Bundle)!,
                parseResult.GetValue(options.PublicKey)!,
                stateDir,
                parseResult.GetValue(options.WorkDirectory)!,
                channelFile,
                parseResult.GetValue(options.Channel),
                kernelChannelFile,
                parseResult.GetValue(options.KernelChannel),
                parseResult.GetValue(options.Esp)!,
                parseResult.GetValue(options.BootEnv)!,
                parseResult.GetValue(options.CurrentOsRelease)!,
                parseResult.GetValue(options.CurrentCmdline)!,
                parseResult.GetValue(options.AllowSequenceBootstrap),
                parseResult.GetValue(options.DryRun),
                RebootValue(parseResult, options),
                parseResult.GetValue(options.DataUnlockMetadata)!,
                parseResult.GetValue(options.CurrentCrypttab)!,
                parseResult.GetValue(options.TargetCrypttab),
                parseResult.GetValue(options.ActiveSlot),
                parseResult.GetValue(options.ActiveBootSlot),
                parseResult.GetValue(options.ActiveRootSlot),
                parseResult.GetValue(options.SuperDevice)!,
                parseResult.GetValue(options.BootDevice),
                parseResult.GetValue(options.RecoveryDevice),
                parseResult.GetValue(options.VbmetaDevice),
                parseResult.GetValue(options.VerifyScript));
        }

        private static bool RebootValue(ParseResult parseResult, CommandOptions options)
        {
            _ = options;
            var lastRebootToken = parseResult.Tokens
                .LastOrDefault(token => token.Value is "--reboot" or "--no-reboot")
                ?.Value;
            return lastRebootToken switch
            {
                "--no-reboot" => false,
                "--reboot" => true,
                _ => true
            };
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
            Argument<string> Bundle,
            Option<string> PublicKey,
            Option<string> StateDir,
            Option<string> WorkDirectory,
            Option<string?> ChannelFile,
            Option<string?> Channel,
            Option<string?> KernelChannelFile,
            Option<string?> KernelChannel,
            Option<string> Esp,
            Option<string> BootEnv,
            Option<string> CurrentOsRelease,
            Option<string> CurrentCmdline,
            Option<bool> AllowSequenceBootstrap,
            Option<bool> DryRun,
            Option<bool> Reboot,
            Option<bool> NoReboot,
            Option<string> DataUnlockMetadata,
            Option<string> CurrentCrypttab,
            Option<string?> TargetCrypttab,
            Option<string?> ActiveSlot,
            Option<string?> ActiveBootSlot,
            Option<string?> ActiveRootSlot,
            Option<string> SuperDevice,
            Option<string?> BootDevice,
            Option<string?> RecoveryDevice,
            Option<string?> VbmetaDevice,
            Option<string?> VerifyScript)
        {
            public void AddTo(Command command)
            {
                command.Arguments.Add(Bundle);
                command.Options.Add(PublicKey);
                command.Options.Add(StateDir);
                command.Options.Add(WorkDirectory);
                command.Options.Add(ChannelFile);
                command.Options.Add(Channel);
                command.Options.Add(KernelChannelFile);
                command.Options.Add(KernelChannel);
                command.Options.Add(Esp);
                command.Options.Add(BootEnv);
                command.Options.Add(CurrentOsRelease);
                command.Options.Add(CurrentCmdline);
                command.Options.Add(AllowSequenceBootstrap);
                command.Options.Add(DryRun);
                command.Options.Add(Reboot);
                command.Options.Add(NoReboot);
                command.Options.Add(DataUnlockMetadata);
                command.Options.Add(CurrentCrypttab);
                command.Options.Add(TargetCrypttab);
                command.Options.Add(ActiveSlot);
                command.Options.Add(ActiveBootSlot);
                command.Options.Add(ActiveRootSlot);
                command.Options.Add(SuperDevice);
                command.Options.Add(BootDevice);
                command.Options.Add(RecoveryDevice);
                command.Options.Add(VbmetaDevice);
                command.Options.Add(VerifyScript);
            }
        }
    }

    private enum OtaType
    {
        FullSystem,
        KernelOnly
    }

    private sealed record OtaAddon(string Key, string File, string Sha256);

    private sealed record OtaApplyPlan(
        string Version,
        long ReleaseSequence,
        long TrustedReleaseFloor,
        string PackageKind,
        string Channel,
        string KernelChannel,
        string BootMode,
        OtaType Type,
        string ActiveBootSlot,
        string TargetBootSlot,
        string ActiveRootSlot,
        string TargetRootSlot,
        string TargetRecoverySlot,
        string SuperDevice,
        string BootDevice,
        string RecoveryDevice,
        string VbmetaDevice,
        string VbmetaPartition,
        string VbmetaDigest,
        string RootLogical,
        string RootDescriptorDigest,
        string ModulesLogical,
        string ModulesDescriptorDigest,
        string FirmwareLogical,
        string FirmwareDescriptorDigest,
        string RootfsHash,
        string ModulesHash,
        string FirmwareHash,
        string RecoveryHash,
        string VbmetaAHash,
        string VbmetaBHash,
        string BootHash,
        string BootloaderHash,
        string FallbackBootHash,
        string MokManagerHash,
        string KernelRelease,
        string CurrentDataUnlockMode,
        string TargetDataUnlockMode,
        string LoaderEntry,
        string EntryId,
        string BootConfig,
        string RootfsPath,
        string RootMapName,
        string ModulesMapName,
        string FirmwareMapName,
        IReadOnlyList<OtaAddon> TargetAddons);
}
