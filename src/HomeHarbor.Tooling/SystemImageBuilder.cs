using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HomeHarbor.Tooling;

public sealed class SystemImageBuilder(
    string root,
    string version,
    SystemImageBuildPlan plan,
    ICommandRunner? runner = null)
{
    private const string DefaultShimRpmUrl = "https://kojipkgs.fedoraproject.org/packages/shim/16.1/8/x86_64/shim-x64-16.1-8.x86_64.rpm";
    private const string DefaultShimRpmSha256 = "ee8787bb9fbd13fcce73de0501938075c24efd78bc1a590826f0c01c7a10986b";
    private const string DefaultShimBootx64 = "usr/lib/efi/shim/16.1-8/EFI/BOOT/BOOTX64.EFI";
    private const string DefaultShimMmx64 = "usr/lib/efi/shim/16.1-8/EFI/fedora/mmx64.efi";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private static readonly string[] FullRootfsRequiredPaths =
    [
        "/",
        "/bin",
        "/boot",
        "/dev",
        "/etc",
        "/etc/crypttab",
        "/etc/fstab",
        "/etc/hostname",
        "/etc/mkinitcpio.conf",
        "/boot/init/homeharbor-verity",
        "/usr/lib/systemd/system/homeharbor-api.service",
        "/usr/lib/systemd/system/homeharbor-postgresql.service",
        "/usr/lib/systemd/system/homeharbor-postgresql-init.service",
        "/usr/lib/systemd/system/homeharbor-postgresql-bootstrap.service",
        "/usr/lib/systemd/system/homeharbor-storage-apply.service",
        "/usr/lib/systemd/system/homeharbor-storage-apply.path",
        "/homeharbor-data",
        "/usr/lib/homeharbor/api/HomeHarbor.Api.dll",
        "/usr/lib/homeharbor/api/HomeHarbor.Tooling.dll",
        "/usr/lib/homeharbor/api/wwwroot/index.html",
        "/usr/lib/dotnet/homeharbor/Npgsql.EntityFrameworkCore.PostgreSQL/10.0.2/Npgsql.EntityFrameworkCore.PostgreSQL.dll",
        "/proc",
        "/run",
        "/sys",
        "/tmp",
        "/opt",
        "/usr",
        "/usr/bin/dotnet",
        "/usr/bin/curl",
        "/usr/bin/jq",
        "/usr/bin/podman",
        "/usr/bin/postgres",
        "/usr/bin/smbd",
        "/usr/bin/lpdump",
        "/usr/bin/openssl",
        "/usr/bin/dmsetup",
        "/usr/lib/modules",
        "/usr/lib/firmware",
        "/usr/lib/systemd/systemd",
        "/usr/lib/systemd/system/homeharbor-boot-attempt.service",
        "/usr/lib/systemd/system/homeharbor-boot-success.service",
        "/usr/lib/systemd/system/homeharbor-smbd.service",
        "/usr/lib/systemd/system/homeharbor-container-apply.path",
        "/usr/lib/systemd/system/homeharbor-system-app-apply.service",
        "/usr/lib/systemd/system/homeharbor-system-app-apply.path",
        "/usr/lib/systemd/system/caddy.service.d/homeharbor-config.conf",
        "/usr/lib/homeharbor/agent/HomeHarbor.Agent",
        "/usr/lib/homeharbor/agent/HomeHarbor.Agent.dll",
        "/usr/lib/homeharbor/agent/HomeHarbor.Agent.deps.json",
        "/usr/lib/homeharbor/agent/HomeHarbor.Agent.runtimeconfig.json",
        "/usr/lib/homeharbor/agent/HomeHarbor.Tooling.dll",
        "/usr/lib/homeharbor/homeharbor-avb",
        "/var"
    ];

    private static readonly string[] RecoveryRootfsRequiredPaths =
    [
        "/",
        "/bin",
        "/dev",
        "/etc",
        "/etc/fstab",
        "/etc/hostname",
        "/proc",
        "/run",
        "/sys",
        "/tmp",
        "/usr",
        "/boot",
        "/boot/recovery_boot.efi",
        "/usr/bin/dotnet",
        "/usr/bin/dmsetup",
        "/usr/bin/lpdump",
        "/usr/lib/homeharbor/recovery/HomeHarbor.Recovery",
        "/usr/lib/homeharbor/recovery/HomeHarbor.Recovery.dll",
        "/usr/lib/homeharbor/recovery/HomeHarbor.Recovery.runtimeconfig.json",
        "/usr/lib/homeharbor/recovery/HomeHarbor.Tooling.dll",
        "/usr/lib/homeharbor/homeharbor-avb",
        "/usr/lib/modules",
        "/usr/lib/firmware",
        "/usr/lib/systemd/systemd",
        "/usr/lib/systemd/system/homeharbor-fastbootd.service",
        "/var"
    ];

    private static readonly string[] ForbiddenRootfsPaths =
    [
        "/usr/bin/zfs",
        "/usr/bin/zpool"
    ];

    private readonly string _root = BuildKeyDefaults.Apply(root);
    private readonly string _artifacts = Path.Combine(Path.GetFullPath(root), "artifacts");
    private readonly string _work = Path.Combine(Path.GetFullPath(root), ".work", "image");
    private readonly ICommandRunner _runner = runner ?? new ProcessCommandRunner();
    private readonly RootlessBuildExecutor _rootless = new(runner ?? new ProcessCommandRunner());

    public async Task BuildAsync(CancellationToken cancellationToken = default)
    {
        var channel = ReleaseChannel.Require(Environment.GetEnvironmentVariable("HOMEHARBOR_CHANNEL") ?? "dev", "HOMEHARBOR_CHANNEL");
        ValidateVersion();
        await _rootless.RequireReadyAsync(cancellationToken);
        ValidateReleaseInputs(channel);
        await ValidateSecureBootAsync(cancellationToken);
        await RequireToolsAsync(cancellationToken);

        var rootfs = Path.Combine(_work, "rootfs");
        var recoveryRootfs = Path.Combine(_work, "recovery-rootfs");
        var packageOutput = Path.Combine(_work, "packages");
        await EnsureNoRootfsApiMountsAsync(cancellationToken);

        await DeleteWorkDirectoryAsync(_work, cancellationToken);
        _ = Directory.CreateDirectory(_artifacts);
        _ = Directory.CreateDirectory(rootfs);
        _ = Directory.CreateDirectory(packageOutput);

        string? shimSource;
        string? mokManagerSource;
        try
        {
            (shimSource, mokManagerSource) = await PrepareShimSourcesAsync(cancellationToken);

            var (ControlPackages, RecoveryPackages) = await BuildPackagesAsync(channel, packageOutput, cancellationToken);
            await BuildRootfsAsync(rootfs, ControlPackages, cancellationToken);
            await BuildRecoveryRootfsBaseAsync(recoveryRootfs, RecoveryPackages, cancellationToken);

            await BuildImagesAsync(rootfs, recoveryRootfs, shimSource, mokManagerSource, cancellationToken);
            await FileWrites.AtomicWriteTextAsync(plan.Artifacts.Plan.Path, JsonSerializer.Serialize(plan, JsonOptions) + "\n", 0644, cancellationToken);
        }
        finally
        {
            await CleanupAsync(cancellationToken);
        }

        Console.WriteLine($"Built OTA input artifacts for {version} in {_artifacts}");
    }

    private async Task BuildImagesAsync(
        string rootfs,
        string recoveryRootfs,
        string? shimSource,
        string? mokManagerSource,
        CancellationToken cancellationToken)
    {
        await RunMappedChrootAsync(rootfs, "mkinitcpio", ["-P"], cancellationToken);

        await EnsureNoRootfsApiMountsAsync(cancellationToken);
        var kernelRelease = KernelRelease(rootfs);
        var vmlinuz = Path.Combine(_work, "vmlinuz-linux");
        var initramfs = Path.Combine(_work, "initramfs-linux.img");
        InstallFile(Path.Combine(rootfs, "boot", "vmlinuz-linux"), vmlinuz, 0644);
        InstallFile(Path.Combine(rootfs, "boot", "initramfs-linux.img"), initramfs, 0644);
        InstallArtifact(vmlinuz, plan.Artifacts.Vmlinuz);
        InstallArtifact(initramfs, plan.Artifacts.Initramfs);

        var modulesRoot = Path.Combine(_work, "modules-root");
        var firmwareRoot = Path.Combine(_work, "firmware-root");
        Directory.Move(Path.Combine(rootfs, "usr", "lib", "modules"), modulesRoot);
        Directory.Move(Path.Combine(rootfs, "usr", "lib", "firmware"), firmwareRoot);
        EnsureDirectory(Path.Combine(rootfs, "usr", "lib", "modules"), 0755);
        EnsureDirectory(Path.Combine(rootfs, "usr", "lib", "firmware"), 0755);
        DeleteDirectory(Path.Combine(recoveryRootfs, "usr", "lib", "modules"));
        DeleteDirectory(Path.Combine(recoveryRootfs, "usr", "lib", "firmware"));
        CopyDirectory(modulesRoot, Path.Combine(recoveryRootfs, "usr", "lib", "modules"));
        CopyDirectory(firmwareRoot, Path.Combine(recoveryRootfs, "usr", "lib", "firmware"));
        RemoveBootKernelArtifacts(Path.Combine(rootfs, "boot"));

        var recoveryBoot = Path.Combine(_work, "recovery_boot.efi");
        await BuildUkiAsync(
            recoveryBoot,
            vmlinuz,
            initramfs,
            Path.Combine(recoveryRootfs, "etc", "os-release"),
            kernelRelease,
            SecureBootAssets.RecoveryCmdline(),
            cancellationToken);
        InstallFile(recoveryBoot, Path.Combine(recoveryRootfs, "boot", "recovery_boot.efi"), 0644);

        var modulesImage = Path.Combine(_work, "modules_a.img");
        var firmwareImage = Path.Combine(_work, "firmware_a.img");
        await RunMappedRootAsync("mkfs.erofs", ["-zlz4hc,12", modulesImage, modulesRoot], cancellationToken);
        await RunMappedRootAsync("mkfs.erofs", ["-zlz4hc,12", firmwareImage, firmwareRoot], cancellationToken);
        await RunAsync("dump.erofs", ["-s", modulesImage], cancellationToken);
        await RunAsync("dump.erofs", ["-s", firmwareImage], cancellationToken);
        InstallArtifact(modulesImage, plan.Artifacts.Modules);
        InstallArtifact(firmwareImage, plan.Artifacts.Firmware);

        ValidateRootfsTree(rootfs, FullRootfsRequiredPaths, ForbiddenRootfsPaths, "full rootfs");
        var rootImage = Path.Combine(_work, "root_a.img");
        await RunMappedRootAsync("mkfs.erofs", ["-zlz4hc,12", rootImage, rootfs], cancellationToken);
        await ValidateErofsRootfsAsync(rootImage, FullRootfsRequiredPaths, ForbiddenRootfsPaths, "full EROFS rootfs", null, cancellationToken);
        InstallArtifact(rootImage, plan.Artifacts.Rootfs);

        ValidateRootfsTree(recoveryRootfs, RecoveryRootfsRequiredPaths, ForbiddenRootfsPaths, "recovery rootfs");
        var recoveryHints = Path.Combine(_work, "recovery-compress-hints");
        await File.WriteAllTextAsync(recoveryHints, "0 boot/recovery_boot[.]efi\n", cancellationToken);
        var recoveryImage = Path.Combine(_work, "recovery.img");
        await RunMappedRootAsync("mkfs.erofs", ["-E^inline_data", "-zlz4hc,12", "--compress-hints=" + recoveryHints, recoveryImage, recoveryRootfs], cancellationToken);
        await ValidateErofsRootfsAsync(recoveryImage, RecoveryRootfsRequiredPaths, ForbiddenRootfsPaths, "recovery EROFS rootfs", null, cancellationToken);
        await ValidateRecoveryBootErofsAsync(recoveryImage, null, cancellationToken);
        var extractedRecoveryBoot = Path.Combine(_work, "recovery_boot.extracted.efi");
        await RunBinaryStdoutToFileAsync("dump.erofs", ["--cat", "--path", "/boot/recovery_boot.efi", recoveryImage], extractedRecoveryBoot, cancellationToken);
        if (!FilesEqual(recoveryBoot, extractedRecoveryBoot))
        {
            throw new InvalidOperationException("recovery EROFS embedded UKI does not match generated recovery_boot.efi");
        }

        InstallArtifact(recoveryImage, plan.Artifacts.Recovery);

        await WriteLogicalImagesAsync(rootImage, recoveryImage, modulesImage, firmwareImage, cancellationToken);
        await BuildAvbImagesAsync(kernelRelease, vmlinuz, initramfs, rootfs, cancellationToken);
        await BuildSuperImageAsync(cancellationToken);
        await BuildBootloaderArtifactsAsync(shimSource, mokManagerSource, cancellationToken);
    }

    private async Task WriteLogicalImagesAsync(
        string rootImage,
        string recoveryImage,
        string modulesImage,
        string firmwareImage,
        CancellationToken cancellationToken)
    {
        await WriteErofsLogicalAsync(rootImage, LogicalPath("root_a"), LogicalDataSize("root_a"), "rootfs", cancellationToken);
        await WriteErofsLogicalAsync(rootImage, LogicalPath("root_b"), LogicalDataSize("root_b"), "rootfs", cancellationToken);
        await WriteErofsLogicalAsync(recoveryImage, LogicalPath("recovery_a"), PartitionDataSize("recovery_a"), "recovery", cancellationToken);
        await WriteErofsLogicalAsync(recoveryImage, LogicalPath("recovery_b"), PartitionDataSize("recovery_b"), "recovery", cancellationToken);
        await WriteErofsLogicalAsync(modulesImage, LogicalPath("modules_a"), LogicalDataSize("modules_a"), "modules", cancellationToken);
        await WriteErofsLogicalAsync(modulesImage, LogicalPath("modules_b"), LogicalDataSize("modules_b"), "modules", cancellationToken);
        await WriteErofsLogicalAsync(firmwareImage, LogicalPath("firmware_a"), LogicalDataSize("firmware_a"), "firmware", cancellationToken);
        await WriteErofsLogicalAsync(firmwareImage, LogicalPath("firmware_b"), LogicalDataSize("firmware_b"), "firmware", cancellationToken);
    }

    private async Task BuildAvbImagesAsync(
        string kernelRelease,
        string vmlinuz,
        string initramfs,
        string rootfs,
        CancellationToken cancellationToken)
    {
        var vbmetaFragments = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["root_a"] = Path.Combine(_work, "root_a.vbmeta"),
            ["root_b"] = Path.Combine(_work, "root_b.vbmeta"),
            ["modules_a"] = Path.Combine(_work, "modules_a.vbmeta"),
            ["modules_b"] = Path.Combine(_work, "modules_b.vbmeta"),
            ["firmware_a"] = Path.Combine(_work, "firmware_a.vbmeta"),
            ["firmware_b"] = Path.Combine(_work, "firmware_b.vbmeta"),
            ["recovery_a"] = Path.Combine(_work, "recovery_a.vbmeta"),
            ["recovery_b"] = Path.Combine(_work, "recovery_b.vbmeta")
        };

        await AvbAddHashtreeAsync(LogicalPath("root_a"), LogicalSize("root_a"), "root", vbmetaFragments["root_a"], cancellationToken);
        await AvbAddHashtreeAsync(LogicalPath("root_b"), LogicalSize("root_b"), "root", vbmetaFragments["root_b"], cancellationToken);
        await AvbAddHashtreeAsync(LogicalPath("modules_a"), LogicalSize("modules_a"), "modules", vbmetaFragments["modules_a"], cancellationToken);
        await AvbAddHashtreeAsync(LogicalPath("modules_b"), LogicalSize("modules_b"), "modules", vbmetaFragments["modules_b"], cancellationToken);
        await AvbAddHashtreeAsync(LogicalPath("firmware_a"), LogicalSize("firmware_a"), "firmware", vbmetaFragments["firmware_a"], cancellationToken);
        await AvbAddHashtreeAsync(LogicalPath("firmware_b"), LogicalSize("firmware_b"), "firmware", vbmetaFragments["firmware_b"], cancellationToken);
        await AvbAddHashtreeAsync(LogicalPath("recovery_a"), PartitionSize("recovery_a"), "recovery", vbmetaFragments["recovery_a"], cancellationToken);
        await AvbAddHashtreeAsync(LogicalPath("recovery_b"), PartitionSize("recovery_b"), "recovery", vbmetaFragments["recovery_b"], cancellationToken);

        Truncate(LogicalPath("root_a"), LogicalSize("root_a"));
        Truncate(LogicalPath("root_b"), LogicalSize("root_b"));
        Truncate(LogicalPath("modules_a"), LogicalSize("modules_a"));
        Truncate(LogicalPath("modules_b"), LogicalSize("modules_b"));
        Truncate(LogicalPath("firmware_a"), LogicalSize("firmware_a"));
        Truncate(LogicalPath("firmware_b"), LogicalSize("firmware_b"));
        Truncate(LogicalPath("recovery_a"), PartitionSize("recovery_a"));
        Truncate(LogicalPath("recovery_b"), PartitionSize("recovery_b"));

        var modulesVerityA = await AvbDescriptorVerityArgAsync(vbmetaFragments["modules_a"], "modules", null, cancellationToken);
        var modulesVerityB = await AvbDescriptorVerityArgAsync(vbmetaFragments["modules_b"], "modules", null, cancellationToken);
        var firmwareVerityA = await AvbDescriptorVerityArgAsync(vbmetaFragments["firmware_a"], "firmware", null, cancellationToken);
        var firmwareVerityB = await AvbDescriptorVerityArgAsync(vbmetaFragments["firmware_b"], "firmware", null, cancellationToken);
        var recoveryVerityA = await AvbDescriptorVerityArgAsync(vbmetaFragments["recovery_a"], "recovery", null, cancellationToken);
        var recoveryVerityB = await AvbDescriptorVerityArgAsync(vbmetaFragments["recovery_b"], "recovery", null, cancellationToken);
        var kernelVerityArgs = KernelVerityCmdlineArgs(modulesVerityA, modulesVerityB, firmwareVerityA, firmwareVerityB, recoveryVerityA, recoveryVerityB);

        var vbmetaA = Path.Combine(_work, "vbmeta_a.img");
        var vbmetaB = Path.Combine(_work, "vbmeta_b.img");
        EnsureSameBytes(vbmetaFragments["root_a"], vbmetaFragments["root_b"], "root AVB descriptors");
        EnsureSameBytes(vbmetaFragments["modules_a"], vbmetaFragments["modules_b"], "modules AVB descriptors");
        EnsureSameBytes(vbmetaFragments["firmware_a"], vbmetaFragments["firmware_b"], "firmware AVB descriptors");
        EnsureSameBytes(vbmetaFragments["recovery_a"], vbmetaFragments["recovery_b"], "recovery AVB descriptors");

        await AvbMakeVbmetaAsync(vbmetaA, [vbmetaFragments["root_a"]], cancellationToken);
        File.Copy(vbmetaA, vbmetaB, overwrite: true);
        var vbmetaDigestA = await AvbVbmetaDigestAsync(vbmetaA, cancellationToken);
        var vbmetaDigestB = await AvbVbmetaDigestAsync(vbmetaB, cancellationToken);
        Truncate(vbmetaA, PartitionSize("vbmeta_a"));
        Truncate(vbmetaB, PartitionSize("vbmeta_b"));

        InstallArtifact(vbmetaA, plan.Artifacts.VbmetaA);
        InstallArtifact(vbmetaB, plan.Artifacts.VbmetaB);
        await WriteDigestArtifactAsync(plan.Artifacts.VbmetaA, vbmetaDigestA, cancellationToken);
        await WriteDigestArtifactAsync(plan.Artifacts.VbmetaB, vbmetaDigestB, cancellationToken);

        var boot = Path.Combine(_work, "boot.efi");
        await BuildUkiAsync(
            boot,
            vmlinuz,
            initramfs,
            Path.Combine(rootfs, "etc", "os-release"),
            kernelRelease,
            SecureBootAssets.GenericBootCmdline(kernelRelease, version, vbmetaDigestA, vbmetaDigestB, kernelVerityArgs),
            cancellationToken);
        InstallArtifact(boot, plan.Artifacts.Boot);
    }

    private async Task BuildSuperImageAsync(CancellationToken cancellationToken)
    {
        var args = new List<string>
        {
            "--metadata-size",
            plan.Super.MetadataSizeBytes.ToString(CultureInfo.InvariantCulture),
            "--metadata-slots",
            plan.Super.MetadataSlots.ToString(CultureInfo.InvariantCulture),
            "--device-size",
            plan.Super.PartitionSizeBytes.ToString(CultureInfo.InvariantCulture),
            "--super-name",
            plan.Super.Name,
            "--group",
            $"{plan.Super.GroupName}:{plan.Super.GroupSizeBytes}"
        };
        foreach (var logical in plan.LogicalPartitions)
        {
            args.Add("--partition");
            args.Add($"{logical.Name}:readonly:{logical.SizeBytes}:{plan.Super.GroupName}");
        }

        foreach (var logical in plan.LogicalPartitions)
        {
            args.Add("--image");
            args.Add($"{logical.Name}={LogicalPath(logical.Name)}");
        }

        var superImage = Path.Combine(_work, "super.img");
        args.Add("--output");
        args.Add(superImage);
        args.Add("--force-full-image");
        await RunAsync("lpmake", args, cancellationToken);
        _ = Directory.CreateDirectory(Path.GetDirectoryName(plan.Artifacts.SuperDump.Path)!);
        await RunAsync("lpdump", [superImage], cancellationToken, stdoutPath: plan.Artifacts.SuperDump.Path);
    }

    private async Task BuildBootloaderArtifactsAsync(
        string? shimSource,
        string? mokManagerSource,
        CancellationToken cancellationToken)
    {
        var mnt = Path.Combine(_work, "mnt");
        _ = Directory.CreateDirectory(mnt);
        var selector = Path.Combine(_work, "HomeHarborBoot.efi");
        await new BuildToolCommands(_root, _runner).BuildEfiLoaderAsync(selector, cancellationToken);
        await InstallBootSelectorAsync(mnt, selector, cancellationToken);
        if (SecureBootAssets.IsEnabled())
        {
            if (string.IsNullOrWhiteSpace(shimSource) || string.IsNullOrWhiteSpace(mokManagerSource))
            {
                throw new InvalidOperationException("secure boot shim and MokManager sources are required when HOMEHARBOR_SECURE_BOOT=1");
            }

            InstallFile(shimSource, Path.Combine(mnt, "EFI", "BOOT", "BOOTX64.EFI"), 0644);
            InstallFile(Path.Combine(mnt, "EFI", "HomeHarbor", "HomeHarborBoot.efi"), Path.Combine(mnt, "EFI", "BOOT", "grubx64.efi"), 0644);
            InstallFile(mokManagerSource, Path.Combine(mnt, "EFI", "BOOT", "mmx64.efi"), 0644);
        }

        InstallArtifact(Path.Combine(mnt, "EFI", "HomeHarbor", "HomeHarborBoot.efi"), plan.Artifacts.Bootloader);
        InstallArtifact(Path.Combine(mnt, "EFI", "BOOT", "BOOTX64.EFI"), plan.Artifacts.Bootx64);
    }

    private async Task BuildRootfsAsync(
        string rootfs,
        IReadOnlyList<string> controlPackages,
        CancellationToken cancellationToken)
    {
        await RunPacstrapAsync(rootfs, plan.Packages.Rootfs, cancellationToken);
        PrepareWritableRootfs(rootfs);
        await RunMappedRootAsync("pacman", ["--root", rootfs, "--noconfirm", "-U", .. controlPackages], cancellationToken);
        InstallPlanDirectories(plan.Rootfs, rootfs);
        var releasePublicKey = Environment.GetEnvironmentVariable("HOMEHARBOR_RELEASE_PUBLIC_KEY");
        if (!string.IsNullOrWhiteSpace(releasePublicKey))
        {
            InstallFile(releasePublicKey, Path.Combine(rootfs, "etc", "homeharbor", "release.pub.pem"), 0644);
        }

        InstallPlanFiles(plan.Rootfs, rootfs);
        await CreatePlanGroupsAsync(plan.Rootfs, rootfs, cancellationToken);
        await CreatePlanUsersAsync(plan.Rootfs, rootfs, cancellationToken);
        await CreatePlanGeneratedUsersAsync(plan.Rootfs, rootfs, cancellationToken);
        ApplyPlanSubIds(plan.Rootfs, rootfs);
        ApplyPlanLinger(plan.Rootfs, rootfs);
        var rootPassword = Environment.GetEnvironmentVariable("HOMEHARBOR_DEBUG_ROOT_PASSWORD");
        if (!string.IsNullOrEmpty(rootPassword))
        {
            await RunMappedChrootAsync(rootfs, "chpasswd", [], cancellationToken, standardInput: $"root:{rootPassword}\n");
        }

        await EnablePlanUnitsAsync(plan.Rootfs, rootfs, cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(rootfs, "etc", "hostname"), plan.Rootfs.Hostname + "\n", cancellationToken);
        if (plan.Rootfs.CreateEmptyCrypttab)
        {
            FileWrites.AtomicWriteText(Path.Combine(rootfs, "etc", "crypttab"), string.Empty, 0644);
        }

        await WritePlanFstabAsync(plan.Rootfs, rootfs, cancellationToken);
        RewriteMkinitcpioHooks(Path.Combine(rootfs, "etc", "mkinitcpio.conf"), plan.Rootfs.MkinitcpioHooks);
    }

    private async Task BuildRecoveryRootfsBaseAsync(
        string recoveryRootfs,
        IReadOnlyList<string> recoveryPackages,
        CancellationToken cancellationToken)
    {
        _ = Directory.CreateDirectory(recoveryRootfs);
        await RunPacstrapAsync(recoveryRootfs, plan.Packages.Recovery, cancellationToken);
        PrepareWritableRootfs(recoveryRootfs);
        await RunMappedRootAsync("pacman", ["--root", recoveryRootfs, "--noconfirm", "-U", .. recoveryPackages], cancellationToken);
        InstallPlanDirectories(plan.Recovery, recoveryRootfs);
        await WritePlanFstabAsync(plan.Recovery, recoveryRootfs, cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(recoveryRootfs, "etc", "hostname"), plan.Recovery.Hostname + "\n", cancellationToken);
        ApplyPlanShells(plan.Recovery, recoveryRootfs);
        await CreatePlanGroupsAsync(plan.Recovery, recoveryRootfs, cancellationToken);
        await CreatePlanUsersAsync(plan.Recovery, recoveryRootfs, cancellationToken);
        await CreatePlanGeneratedUsersAsync(plan.Recovery, recoveryRootfs, cancellationToken);
        ApplyPlanSubIds(plan.Recovery, recoveryRootfs);
        ApplyPlanLinger(plan.Recovery, recoveryRootfs);
        await EnablePlanUnitsAsync(plan.Recovery, recoveryRootfs, cancellationToken);
    }

    private async Task<(IReadOnlyList<string> ControlPackages, IReadOnlyList<string> RecoveryPackages)> BuildPackagesAsync(
        string channel,
        string packageOutput,
        CancellationToken cancellationToken)
    {
        var environment = new Dictionary<string, string>
        {
            ["HOMEHARBOR_PACKAGE_OUTPUT"] = packageOutput,
            ["HOMEHARBOR_PACKAGE_WORK"] = Path.Combine(_work, "arch-package"),
            ["HOMEHARBOR_CHANNEL"] = channel
        };
        var previousEnvironment = environment.ToDictionary(pair => pair.Key, pair => Environment.GetEnvironmentVariable(pair.Key), StringComparer.Ordinal);
        try
        {
            foreach (var (key, value) in environment)
            {
                Environment.SetEnvironmentVariable(key, value);
            }

            await new BuildToolCommands(_root, _runner).ArchPackageAsync(version, cancellationToken);
        }
        finally
        {
            foreach (var (key, value) in previousEnvironment)
            {
                Environment.SetEnvironmentVariable(key, value);
            }
        }
        var packages = Directory.GetFiles(packageOutput, "*.pkg.tar.*", SearchOption.TopDirectoryOnly)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (packages.Length == 0)
        {
            throw new InvalidOperationException($"no HomeHarbor packages were produced in {packageOutput}");
        }

        var controlPackages = new List<string>();
        var recoveryPackages = new List<string>();
        foreach (var package in packages)
        {
            var name = Path.GetFileName(package);
            if (name.StartsWith("dotnet-", StringComparison.Ordinal))
            {
                controlPackages.Add(package);
                recoveryPackages.Add(package);
            }
            else if (name.StartsWith("homeharbor-recovery-", StringComparison.Ordinal))
            {
                recoveryPackages.Add(package);
            }
            else if (!name.StartsWith("homeharbor-installer-", StringComparison.Ordinal))
            {
                controlPackages.Add(package);
            }
        }

        return controlPackages.Count == 0
            ? throw new InvalidOperationException($"no HomeHarbor control packages were produced in {packageOutput}")
            : !recoveryPackages.Any(package => Path.GetFileName(package).StartsWith("homeharbor-recovery-", StringComparison.Ordinal))
            ? throw new InvalidOperationException("expected one HomeHarbor recovery package, found none")
            : ((IReadOnlyList<string> ControlPackages, IReadOnlyList<string> RecoveryPackages))(controlPackages, recoveryPackages);
    }

    private void ValidateReleaseInputs(string channel)
    {
        var releasePublicKey = Environment.GetEnvironmentVariable("HOMEHARBOR_RELEASE_PUBLIC_KEY");
        var secureBootKey = Environment.GetEnvironmentVariable("HOMEHARBOR_SECURE_BOOT_KEY");
        if (channel != ReleaseChannel.Dev && string.IsNullOrWhiteSpace(releasePublicKey))
        {
            throw new InvalidOperationException(
                $"Refusing to build {channel} OTA inputs {version} without HOMEHARBOR_RELEASE_PUBLIC_KEY. " +
                "Set HOMEHARBOR_RELEASE_PUBLIC_KEY to the Ed25519 update-channel public key.");
        }

        if (channel != ReleaseChannel.Dev && string.IsNullOrWhiteSpace(secureBootKey))
        {
            throw new InvalidOperationException($"Refusing to build {channel} OTA inputs {version} without HOMEHARBOR_SECURE_BOOT_KEY for AVB signing.");
        }

        if (channel != ReleaseChannel.Dev && Env.Flag("HOMEHARBOR_ALLOW_UNSIGNED"))
        {
            throw new InvalidOperationException($"Refusing to build {channel} OTA inputs with HOMEHARBOR_ALLOW_UNSIGNED=1.");
        }

        if (channel != ReleaseChannel.Dev && Env.Flag("HOMEHARBOR_AVB_ALLOW_UNSIGNED"))
        {
            throw new InvalidOperationException($"Refusing to build {channel} OTA inputs with HOMEHARBOR_AVB_ALLOW_UNSIGNED=1.");
        }

        if (!string.IsNullOrWhiteSpace(releasePublicKey))
        {
            if (!File.Exists(releasePublicKey))
            {
                throw new FileNotFoundException("HOMEHARBOR_RELEASE_PUBLIC_KEY does not point to a readable file", releasePublicKey);
            }
        }

        if (!string.IsNullOrWhiteSpace(secureBootKey) && !File.Exists(secureBootKey))
        {
            throw new FileNotFoundException("HOMEHARBOR_SECURE_BOOT_KEY does not point to a readable file", secureBootKey);
        }
    }

    private async Task ValidateSecureBootAsync(CancellationToken cancellationToken)
    {
        _ = await FindUkifyAsync(cancellationToken);
        if (!SecureBootAssets.IsEnabled())
        {
            return;
        }

        _ = SecureBootAssets.RequireSigningAssets();
        var enrollMode = SecureBootAssets.EnrollMode();
        if (enrollMode is not ("manual" or "force" or "off"))
        {
            throw new InvalidOperationException($"HOMEHARBOR_SECURE_BOOT_ENROLL must be manual, force, or off; got: {enrollMode}");
        }

        await NeedAsync("sbsign", cancellationToken);
        await NeedAsync("openssl", cancellationToken);
        if (!string.Equals(enrollMode, "off", StringComparison.Ordinal))
        {
            await NeedAsync("sbsiglist", cancellationToken);
            await NeedAsync("sbvarsign", cancellationToken);
            await NeedAsync("systemd-id128", cancellationToken);
        }

        var shimSource = Env.Optional("HOMEHARBOR_IMAGE_SHIM_SOURCE") ?? Env.Optional("HOMEHARBOR_OTA_SHIM_SOURCE");
        var mokManagerSource = Env.Optional("HOMEHARBOR_IMAGE_MOK_MANAGER_SOURCE") ?? Env.Optional("HOMEHARBOR_OTA_MOK_MANAGER_SOURCE") ?? Env.Optional("HOMEHARBOR_OTA_MMX64_SOURCE");
        if (!string.IsNullOrWhiteSpace(shimSource) || !string.IsNullOrWhiteSpace(mokManagerSource))
        {
            if (string.IsNullOrWhiteSpace(shimSource) || !File.Exists(shimSource))
            {
                throw new InvalidOperationException("HOMEHARBOR_IMAGE_SHIM_SOURCE or HOMEHARBOR_OTA_SHIM_SOURCE must point to a Microsoft-signed shim when HOMEHARBOR_SECURE_BOOT=1");
            }

            if (string.IsNullOrWhiteSpace(mokManagerSource) || !File.Exists(mokManagerSource))
            {
                throw new InvalidOperationException("HOMEHARBOR_IMAGE_MOK_MANAGER_SOURCE or HOMEHARBOR_OTA_MOK_MANAGER_SOURCE must point to MokManager/mmx64.efi when HOMEHARBOR_SECURE_BOOT=1");
            }
        }
    }

    private async Task<(string? ShimSource, string? MokManagerSource)> PrepareShimSourcesAsync(CancellationToken cancellationToken)
    {
        if (!SecureBootAssets.IsEnabled())
        {
            return (null, null);
        }

        var shimSource = Env.Optional("HOMEHARBOR_IMAGE_SHIM_SOURCE") ?? Env.Optional("HOMEHARBOR_OTA_SHIM_SOURCE");
        var mokManagerSource = Env.Optional("HOMEHARBOR_IMAGE_MOK_MANAGER_SOURCE") ?? Env.Optional("HOMEHARBOR_OTA_MOK_MANAGER_SOURCE") ?? Env.Optional("HOMEHARBOR_OTA_MMX64_SOURCE");
        if (!string.IsNullOrWhiteSpace(shimSource) || !string.IsNullOrWhiteSpace(mokManagerSource))
        {
            return string.IsNullOrWhiteSpace(shimSource) || !File.Exists(shimSource)
                ? throw new InvalidOperationException("shim source does not point to a readable file: " + (shimSource ?? "missing"))
                : string.IsNullOrWhiteSpace(mokManagerSource) || !File.Exists(mokManagerSource)
                ? throw new InvalidOperationException("MokManager source does not point to a readable file: " + (mokManagerSource ?? "missing"))
                : ((string? ShimSource, string? MokManagerSource))(Path.GetFullPath(shimSource), Path.GetFullPath(mokManagerSource));
        }

        var output = Path.Combine(_work, "shim");
        _ = Directory.CreateDirectory(output);
        var rpmPath = Path.Combine(output, "shim.rpm");
        var rpmSource = Env.Optional("HOMEHARBOR_SHIM_RPM");
        if (!string.IsNullOrWhiteSpace(rpmSource))
        {
            if (!File.Exists(rpmSource))
            {
                throw new FileNotFoundException("HOMEHARBOR_SHIM_RPM does not point to a readable file", rpmSource);
            }

            File.Copy(rpmSource, rpmPath, overwrite: true);
        }
        else
        {
            var rpmUrl = Env.String("HOMEHARBOR_SHIM_RPM_URL", DefaultShimRpmUrl);
            Console.Error.WriteLine($"Downloading Microsoft-signed shim RPM from {rpmUrl}");
            if (await CommandExistsAsync("curl", cancellationToken))
            {
                await RunAsync("curl", ["-fsSL", rpmUrl, "-o", rpmPath], cancellationToken);
            }
            else if (await CommandExistsAsync("wget", cancellationToken))
            {
                await RunAsync("wget", ["-qO", rpmPath, rpmUrl], cancellationToken);
            }
            else
            {
                throw new InvalidOperationException("missing required download tool: curl or wget");
            }
        }

        var expectedSha = Env.String("HOMEHARBOR_SHIM_RPM_SHA256", DefaultShimRpmSha256).ToLowerInvariant();
        var actualSha = Sha256Hex(rpmPath);
        if (!string.Equals(actualSha, expectedSha, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"shim RPM SHA256 mismatch: expected {expectedSha}, got {actualSha}");
        }

        await NeedAsync("bsdtar", cancellationToken);
        var extractDir = Path.Combine(output, "rpm-root");
        DeleteDirectory(extractDir);
        _ = Directory.CreateDirectory(extractDir);
        await RunAsync("bsdtar", ["-xf", rpmPath, "-C", extractDir], cancellationToken);
        var shim = Path.Combine(output, "BOOTX64.EFI");
        var mok = Path.Combine(output, "mmx64.efi");
        InstallFile(Path.Combine(extractDir, DefaultShimBootx64), shim, 0644);
        InstallFile(Path.Combine(extractDir, DefaultShimMmx64), mok, 0644);
        return new FileInfo(shim).Length == 0 || new FileInfo(mok).Length == 0
            ? throw new InvalidOperationException("extracted shim RPM did not contain non-empty BOOTX64.EFI and mmx64.efi assets")
            : ((string? ShimSource, string? MokManagerSource))(shim, mok);
    }

    private async Task RequireToolsAsync(CancellationToken cancellationToken)
    {
        foreach (var tool in new[] { "avbtool", "makepkg", "pacman", "pnpm", "mkfs.erofs", "dump.erofs", "lpmake", "lpdump", "cc" })
        {
            await NeedAsync(tool, cancellationToken);
        }
    }

    private async Task NeedAsync(string command, CancellationToken cancellationToken)
    {
        if (!await CommandExistsAsync(command, cancellationToken))
        {
            throw new InvalidOperationException("missing required tool: " + command);
        }
    }

    private async Task<bool> CommandExistsAsync(string command, CancellationToken cancellationToken)
    {
        var result = await _runner.RunAsync("sh", ["-c", "command -v \"$1\" >/dev/null 2>&1", "sh", command], new CommandRunOptions(ThrowOnStartFailure: false), cancellationToken);
        return result.ExitCode == 0;
    }

    private async Task RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken,
        string? standardInput = null,
        string? stdoutPath = null,
        IReadOnlyDictionary<string, string>? environment = null,
        bool allowFailure = false)
    {
        var result = await _runner.RunAsync(
            fileName,
            arguments,
            new CommandRunOptions(
                StandardInput: standardInput,
                StreamOutput: stdoutPath is null,
                StreamError: true,
                EnvironmentOverride: environment),
            cancellationToken);
        if (!allowFailure)
        {
            _ = result.EnsureSuccess();
        }

        if (stdoutPath is not null)
        {
            _ = Directory.CreateDirectory(Path.GetDirectoryName(stdoutPath)!);
            await File.WriteAllTextAsync(stdoutPath, result.Stdout, cancellationToken);
        }
    }

    private async Task RunPacstrapAsync(
        string rootfs,
        IEnumerable<string> packages,
        CancellationToken cancellationToken,
        string? standardInput = null)
    {
        var result = await _rootless.RunPacstrapAsync(
            rootfs,
            packages,
            new CommandRunOptions(StandardInput: standardInput, StreamOutput: true, StreamError: true),
            cancellationToken);
        _ = result.EnsureSuccess();
    }

    private async Task RunMappedRootAsync(
        string fileName,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken,
        string? standardInput = null,
        bool allowFailure = false)
    {
        var result = await _rootless.RunMappedRootAsync(
            fileName,
            arguments,
            new CommandRunOptions(StandardInput: standardInput, StreamOutput: true, StreamError: true),
            cancellationToken);
        if (!allowFailure)
        {
            _ = result.EnsureSuccess();
        }
    }

    private async Task RunMappedChrootAsync(
        string rootfs,
        string fileName,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken,
        string? standardInput = null,
        bool allowFailure = false)
    {
        var result = await _rootless.RunMappedChrootAsync(
            rootfs,
            fileName,
            arguments,
            new CommandRunOptions(StandardInput: standardInput, StreamOutput: true, StreamError: true),
            cancellationToken);
        if (!allowFailure)
        {
            _ = result.EnsureSuccess();
        }
    }

    private async Task<string> CaptureAsync(string fileName, IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        var result = await _runner.RunAsync(fileName, arguments, new CommandRunOptions(StreamError: true), cancellationToken);
        _ = result.EnsureSuccess();
        return result.Stdout.Trim();
    }

    private static async Task RunBinaryStdoutToFileAsync(
        string fileName,
        IEnumerable<string> arguments,
        string stdoutPath,
        CancellationToken cancellationToken)
    {
        _ = Directory.CreateDirectory(Path.GetDirectoryName(stdoutPath)!);
        var start = new ProcessStartInfo(fileName)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Failed to start " + fileName);
        await using var output = File.Create(stdoutPath);
        var stdout = process.StandardOutput.BaseStream.CopyToAsync(output, cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        await stdout;
        var error = await stderr;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Command failed: {ProcessCommandRunner.FormatCommand(fileName, arguments)} exited {process.ExitCode}" +
                (string.IsNullOrWhiteSpace(error) ? string.Empty : Environment.NewLine + error.Trim()));
        }
    }

    private async Task BuildUkiAsync(
        string output,
        string linux,
        string initrd,
        string osRelease,
        string uname,
        string cmdline,
        CancellationToken cancellationToken)
    {
        var ukify = await FindUkifyAsync(cancellationToken);
        var cmdlineFile = Path.Combine(_work, "cmdline-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(cmdlineFile, cmdline + "\n", cancellationToken);
        var args = new List<string>
        {
            "build",
            "--linux=" + linux,
            "--initrd=" + initrd,
            "--os-release=@" + osRelease,
            "--uname=" + uname,
            "--cmdline=@" + cmdlineFile,
            "--output=" + output
        };
        if (SecureBootAssets.IsEnabled())
        {
            var (Key, Certificate) = SecureBootAssets.RequireSigningAssets();
            args.Insert(args.Count - 1, "--secureboot-private-key=" + Key);
            args.Insert(args.Count - 1, "--secureboot-certificate=" + Certificate);
        }

        await RunAsync(ukify, args, cancellationToken);
        File.Delete(cmdlineFile);
    }

    private async Task<string> FindUkifyAsync(CancellationToken cancellationToken)
    {
        return await CommandExistsAsync("ukify", cancellationToken)
            ? "ukify"
            : File.Exists("/usr/lib/systemd/ukify")
            ? "/usr/lib/systemd/ukify"
            : throw new InvalidOperationException("missing required Secure Boot tool: ukify or /usr/lib/systemd/ukify");
    }

    private async Task AvbAddHashtreeAsync(
        string image,
        long partitionSize,
        string partitionName,
        string outputVbmeta,
        CancellationToken cancellationToken)
    {
        await RunAsync(
            "avbtool",
            [
                "add_hashtree_footer",
                "--image",
                image,
                "--partition_size",
                partitionSize.ToString(CultureInfo.InvariantCulture),
                "--partition_name",
                partitionName,
                "--hash_algorithm",
                "sha256",
                "--salt",
                AvbSalt(partitionName),
                "--do_not_generate_fec",
                "--do_not_use_ab",
                "--output_vbmeta_image",
                outputVbmeta,
                "--do_not_append_vbmeta_image",
                "--algorithm",
                "NONE"
            ],
            cancellationToken);
    }

    private static string AvbSalt(string partitionName)
        => Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes("homeharbor-avb:" + partitionName))).ToLowerInvariant();

    private static void EnsureSameBytes(string left, string right, string label)
    {
        using var leftStream = File.OpenRead(left);
        using var rightStream = File.OpenRead(right);
        if (leftStream.Length != rightStream.Length)
        {
            throw new InvalidOperationException(label + " differ across A/B slots");
        }

        var leftBuffer = new byte[8192];
        var rightBuffer = new byte[leftBuffer.Length];
        while (true)
        {
            var leftRead = leftStream.Read(leftBuffer, 0, leftBuffer.Length);
            var rightRead = rightStream.Read(rightBuffer, 0, rightBuffer.Length);
            if (leftRead != rightRead)
            {
                throw new InvalidOperationException(label + " differ across A/B slots");
            }

            if (leftRead == 0)
            {
                return;
            }

            if (!leftBuffer.AsSpan(0, leftRead).SequenceEqual(rightBuffer.AsSpan(0, rightRead)))
            {
                throw new InvalidOperationException(label + " differ across A/B slots");
            }
        }
    }

    private async Task AvbMakeVbmetaAsync(
        string output,
        IReadOnlyList<string> descriptorImages,
        CancellationToken cancellationToken)
    {
        var args = new List<string> { "make_vbmeta_image", "--output", output };
        args.AddRange(AvbSigningArgs());
        foreach (var descriptor in descriptorImages)
        {
            args.Add("--include_descriptors_from_image");
            args.Add(descriptor);
        }

        await RunAsync("avbtool", args, cancellationToken);
    }

    private static IReadOnlyList<string> AvbSigningArgs()
    {
        var key = Environment.GetEnvironmentVariable("HOMEHARBOR_SECURE_BOOT_KEY");
        return !string.IsNullOrWhiteSpace(key)
            ? ["--algorithm", Env.String("HOMEHARBOR_AVB_ALGORITHM", "SHA256_RSA2048"), "--key", key]
            : Env.Flag("HOMEHARBOR_AVB_ALLOW_UNSIGNED") || Env.Flag("HOMEHARBOR_ALLOW_UNSIGNED")
            ? ["--algorithm", "NONE"]
            : throw new InvalidOperationException("HOMEHARBOR_SECURE_BOOT_KEY is required for signed vbmeta; set HOMEHARBOR_AVB_ALLOW_UNSIGNED=1 only for explicit unsigned development builds");
    }

    private async Task<string> AvbVbmetaDigestAsync(string image, CancellationToken cancellationToken)
    {
        var output = await CaptureAsync("avbtool", ["calculate_vbmeta_digest", "--image", image], cancellationToken);
        return output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()
            ?? throw new InvalidOperationException("avbtool calculate_vbmeta_digest produced no digest");
    }

    private async Task<string> AvbDescriptorVerityArgAsync(
        string image,
        string partition,
        string? expectedDigest,
        CancellationToken cancellationToken)
    {
        var helper = await AvbHelperPathAsync(cancellationToken);
        var args = expectedDigest is null
            ? new[] { "descriptor", image, partition }
            : ["descriptor", image, partition, expectedDigest];
        var output = await CaptureAsync(helper, args, cancellationToken);
        var values = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('=', 2))
            .Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0].Trim(), parts => parts[1].Trim(), StringComparer.Ordinal);
        return string.Join(
            ':',
            RequiredDescriptorValue(values, "HASH_ALGORITHM"),
            RequiredDescriptorValue(values, "DATA_BLOCK_SIZE"),
            RequiredDescriptorValue(values, "HASH_BLOCK_SIZE"),
            RequiredDescriptorValue(values, "DATA_BLOCKS"),
            RequiredDescriptorValue(values, "TREE_OFFSET"),
            RequiredDescriptorValue(values, "SALT"),
            RequiredDescriptorValue(values, "ROOT_DIGEST"));
    }

    private async Task<string> AvbHelperPathAsync(CancellationToken cancellationToken)
    {
        var helper = Environment.GetEnvironmentVariable("HOMEHARBOR_AVB_HELPER");
        if (!string.IsNullOrWhiteSpace(helper))
        {
            return !File.Exists(helper) ? throw new FileNotFoundException("HOMEHARBOR_AVB_HELPER is not executable", helper) : helper;
        }

        helper = Path.Combine(_work, "homeharbor-avb");
        if (!File.Exists(helper))
        {
            await BuildAvbHelperAsync(helper, cancellationToken);
        }

        return helper;
    }

    private async Task BuildAvbHelperAsync(string output, CancellationToken cancellationToken)
    {
        _ = Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        await RunAsync("cc", ["-O2", "-Wall", "-Wextra", "-o", output, Path.Combine(_root, "boot", "avb", "homeharbor-avb.c"), "-lcrypto"], cancellationToken);
        SetMode(output, 0755);
    }

    private static string RequiredDescriptorValue(IReadOnlyDictionary<string, string> values, string name)
        => values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidOperationException("AVB descriptor output is missing " + name);

    private static string KernelVerityCmdlineArgs(params string?[] values)
    {
        var names = new[] { "modules_a", "modules_b", "firmware_a", "firmware_b", "recovery_a", "recovery_b" };
        var args = new List<string>();
        for (var i = 0; i < names.Length; i++)
        {
            if (!string.IsNullOrWhiteSpace(values[i]))
            {
                args.Add($"homeharbor.{names[i]}_verity={values[i]}");
            }
        }

        return string.Join(' ', args);
    }

    private async Task InstallBootSelectorAsync(string esp, string selector, CancellationToken cancellationToken)
    {
        var homeHarborBoot = Path.Combine(esp, "EFI", "HomeHarbor", "HomeHarborBoot.efi");
        var bootx64 = Path.Combine(esp, "EFI", "BOOT", "BOOTX64.EFI");
        InstallFile(selector, homeHarborBoot, 0644);
        InstallFile(selector, bootx64, 0644);
        await SignEfiFileAsync(homeHarborBoot, cancellationToken);
        await SignEfiFileAsync(bootx64, cancellationToken);
    }

    private async Task SignEfiFileAsync(string path, CancellationToken cancellationToken)
    {
        if (!SecureBootAssets.IsEnabled() || !File.Exists(path))
        {
            return;
        }

        var (Key, Certificate) = SecureBootAssets.RequireSigningAssets();
        var temp = path + ".signed." + Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
        try
        {
            await RunAsync("sbsign", ["--key", Key, "--cert", Certificate, "--output", temp, path], cancellationToken);
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

    private async Task ValidateErofsRootfsAsync(
        string image,
        IReadOnlyList<string> requiredPaths,
        IReadOnlyList<string> forbiddenPaths,
        string label,
        string? offset,
        CancellationToken cancellationToken)
    {
        foreach (var path in requiredPaths)
        {
            if (!await ErofsPathExistsAsync(image, path, offset, cancellationToken))
            {
                throw new InvalidOperationException($"{label} validation failed: missing {path}");
            }
        }

        foreach (var path in forbiddenPaths)
        {
            if (await ErofsPathExistsAsync(image, path, offset, cancellationToken))
            {
                throw new InvalidOperationException($"{label} validation failed: forbidden path {path}");
            }
        }
    }

    private async Task ValidateRecoveryBootErofsAsync(
        string image,
        string? offset,
        CancellationToken cancellationToken)
    {
        var args = ErofsDumpArgs(image, "/boot/recovery_boot.efi", offset);
        var result = await _runner.RunAsync("dump.erofs", args, new CommandRunOptions(StreamError: false), cancellationToken);
        if (result.ExitCode != 0 || result.CombinedOutput.Contains("<E> erofs:", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("recovery EROFS validation failed: missing /boot/recovery_boot.efi");
        }

        if (!result.CombinedOutput.Contains("regular file", StringComparison.Ordinal) ||
            !result.CombinedOutput.Contains("Layout: 0", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("recovery EROFS validation failed: /boot/recovery_boot.efi must be an uncompressed flat file");
        }
    }

    private async Task<bool> ErofsPathExistsAsync(string image, string path, string? offset, CancellationToken cancellationToken)
    {
        var result = await _runner.RunAsync("dump.erofs", ErofsDumpArgs(image, path, offset), new CommandRunOptions(StreamError: false), cancellationToken);
        return result.ExitCode == 0 && !result.CombinedOutput.Contains("<E> erofs:", StringComparison.Ordinal);
    }

    private static IReadOnlyList<string> ErofsDumpArgs(string image, string path, string? offset)
    {
        var args = new List<string>();
        if (!string.IsNullOrWhiteSpace(offset))
        {
            args.Add("--offset=" + offset);
        }

        args.Add("--path");
        args.Add(path);
        args.Add(image);
        return args;
    }

    private static void ValidateRootfsTree(
        string root,
        IReadOnlyList<string> requiredPaths,
        IReadOnlyList<string> forbiddenPaths,
        string label)
    {
        foreach (var path in requiredPaths)
        {
            if (!PathExistsInRoot(root, path))
            {
                throw new InvalidOperationException($"{label} validation failed: missing {path}");
            }
        }

        foreach (var path in forbiddenPaths)
        {
            if (PathExistsInRoot(root, path))
            {
                throw new InvalidOperationException($"{label} validation failed: forbidden path {path}");
            }
        }
    }

    private static bool PathExistsInRoot(string root, string imagePath)
    {
        var local = Path.Combine(root, imagePath.TrimStart('/'));
        return File.Exists(local) || Directory.Exists(local) || File.Exists(local) || new FileInfo(local).LinkTarget is not null || new DirectoryInfo(local).LinkTarget is not null;
    }

    private static void InstallPlanDirectories(SystemImageRootPlan rootPlan, string rootfs)
    {
        foreach (var directory in rootPlan.Directories)
        {
            EnsureDirectory(ImagePath(rootfs, directory), 0755);
        }
    }

    private static void InstallPlanFiles(SystemImageRootPlan rootPlan, string rootfs)
    {
        foreach (var file in rootPlan.Files)
        {
            var target = ImagePath(rootfs, file.Destination);
            if (!string.IsNullOrWhiteSpace(file.Source))
            {
                InstallFile(file.Source, target, ParseMode(file.Mode));
            }
            else
            {
                _ = Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                var content = file.Content ?? string.Empty;
                if (!content.EndsWith('\n'))
                {
                    content += "\n";
                }

                File.WriteAllText(target, content, new UTF8Encoding(false));
                SetMode(target, ParseMode(file.Mode));
            }
        }
    }

    private static async Task WritePlanFstabAsync(SystemImageRootPlan rootPlan, string rootfs, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        foreach (var entry in rootPlan.Fstab)
        {
            _ = builder.Append(entry.Spec)
                .Append(' ')
                .Append(entry.MountPoint)
                .Append(' ')
                .Append(entry.FileSystem)
                .Append(' ')
                .Append(entry.Options)
                .Append(' ')
                .Append(entry.Dump.ToString(CultureInfo.InvariantCulture))
                .Append(' ')
                .Append(entry.Pass.ToString(CultureInfo.InvariantCulture))
                .Append('\n');
        }

        await File.WriteAllTextAsync(Path.Combine(rootfs, "etc", "fstab"), builder.ToString(), cancellationToken);
    }

    private static void ApplyPlanShells(SystemImageRootPlan rootPlan, string rootfs)
    {
        var path = Path.Combine(rootfs, "etc", "shells");
        var existing = File.Exists(path)
            ? File.ReadAllLines(path).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        foreach (var shell in rootPlan.Shells)
        {
            if (existing.Add(shell))
            {
                File.AppendAllText(path, shell + "\n");
            }
        }
    }

    private async Task CreatePlanGroupsAsync(SystemImageRootPlan rootPlan, string rootfs, CancellationToken cancellationToken)
    {
        foreach (var group in rootPlan.Groups)
        {
            var args = new List<string>();
            if (group.System)
            {
                args.Add("--system");
            }

            args.Add(group.Name);
            await RunMappedChrootAsync(rootfs, "groupadd", args, cancellationToken, allowFailure: true);
        }
    }

    private async Task CreatePlanUsersAsync(SystemImageRootPlan rootPlan, string rootfs, CancellationToken cancellationToken)
    {
        foreach (var user in rootPlan.Users)
        {
            await CreateUserAsync(rootfs, user.Name, user.System, user.UserGroup, user.HomeDir, user.CreateHome, user.Shell, user.Groups, cancellationToken);
        }
    }

    private async Task CreatePlanGeneratedUsersAsync(SystemImageRootPlan rootPlan, string rootfs, CancellationToken cancellationToken)
    {
        foreach (var user in rootPlan.GeneratedUsers)
        {
            for (var index = user.Start; index <= user.End; index++)
            {
                var suffix = index.ToString("D" + user.Width.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
                await CreateUserAsync(rootfs, user.Prefix + suffix, user.System, false, user.HomeDir, user.CreateHome, user.Shell, user.Groups, cancellationToken);
            }
        }
    }

    private async Task CreateUserAsync(
        string rootfs,
        string name,
        bool system,
        bool userGroup,
        string? homeDir,
        bool createHome,
        string? shell,
        IReadOnlyList<string> groups,
        CancellationToken cancellationToken)
    {
        var args = new List<string>();
        if (system)
        {
            args.Add("--system");
        }

        if (userGroup)
        {
            args.Add("--user-group");
        }

        if (!string.IsNullOrWhiteSpace(homeDir))
        {
            args.Add("--home-dir");
            args.Add(homeDir);
        }

        if (createHome)
        {
            args.Add("--create-home");
        }

        if (!string.IsNullOrWhiteSpace(shell))
        {
            args.Add("--shell");
            args.Add(shell);
        }

        if (groups.Count > 0)
        {
            args.Add("--groups");
            args.Add(string.Join(',', groups));
        }

        args.Add(name);
        await RunMappedChrootAsync(rootfs, "useradd", args, cancellationToken, allowFailure: true);
    }

    private static void ApplyPlanSubIds(SystemImageRootPlan rootPlan, string rootfs)
    {
        var subuid = Path.Combine(rootfs, "etc", "subuid");
        var subgid = Path.Combine(rootfs, "etc", "subgid");
        foreach (var subId in rootPlan.SubIds)
        {
            AppendLineIfMissingPrefix(subuid, subId.Name + ":", $"{subId.Name}:{subId.Start}:{subId.Count}");
            AppendLineIfMissingPrefix(subgid, subId.Name + ":", $"{subId.Name}:{subId.Start}:{subId.Count}");
        }
    }

    private static void ApplyPlanLinger(SystemImageRootPlan rootPlan, string rootfs)
    {
        var linger = Path.Combine(rootfs, "var", "lib", "systemd", "linger");
        EnsureDirectory(linger, 0755);
        foreach (var user in rootPlan.LingerUsers)
        {
            FileWrites.AtomicWriteText(Path.Combine(linger, user), string.Empty, 0644);
        }
    }

    private async Task EnablePlanUnitsAsync(SystemImageRootPlan rootPlan, string rootfs, CancellationToken cancellationToken)
    {
        if (rootPlan.SystemdUnits.Count == 0)
        {
            return;
        }

        await RunMappedChrootAsync(rootfs, "systemctl", ["enable", .. rootPlan.SystemdUnits], cancellationToken);
    }

    private static async Task WriteErofsLogicalAsync(
        string erofsImage,
        string logicalImage,
        long logicalBytes,
        string label,
        CancellationToken cancellationToken)
    {
        var erofsSize = new FileInfo(erofsImage).Length;
        if (erofsSize > logicalBytes)
        {
            throw new InvalidOperationException($"{label} EROFS is larger than its Android super logical area: {erofsSize} > {logicalBytes}");
        }

        _ = Directory.CreateDirectory(Path.GetDirectoryName(logicalImage)!);
        await using var logical = new FileStream(logicalImage, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        logical.SetLength(logicalBytes);
        await using var erofs = File.OpenRead(erofsImage);
        await erofs.CopyToAsync(logical, cancellationToken);
    }

    private static void InstallArtifact(string source, SystemImageArtifactPlan artifact)
    {
        InstallFile(source, artifact.Path, 0644);
        if (!string.IsNullOrWhiteSpace(artifact.Sha256Path))
        {
            _ = Directory.CreateDirectory(Path.GetDirectoryName(artifact.Sha256Path)!);
            File.WriteAllText(artifact.Sha256Path, Sha256Hex(artifact.Path) + "\n");
        }
    }

    private static async Task WriteDigestArtifactAsync(
        SystemImageArtifactPlan artifact,
        string digest,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(artifact.DigestPath))
        {
            return;
        }

        _ = Directory.CreateDirectory(Path.GetDirectoryName(artifact.DigestPath)!);
        await File.WriteAllTextAsync(artifact.DigestPath, digest + "\n", cancellationToken);
    }

    private async Task EnsureNoRootfsApiMountsAsync(CancellationToken cancellationToken)
    {
        foreach (var rootPath in new[] { Path.Combine(_work, "rootfs"), Path.Combine(_work, "recovery-rootfs") })
        {
            foreach (var mountPath in new[]
            {
                Path.Combine(rootPath, "run"),
                Path.Combine(rootPath, "dev", "pts"),
                Path.Combine(rootPath, "dev"),
                Path.Combine(rootPath, "proc"),
                Path.Combine(rootPath, "sys")
            })
            {
                if (await IsMountPointAsync(mountPath, cancellationToken))
                {
                    throw new InvalidOperationException(
                        "stale rootful image-builder mount detected at " + mountPath +
                        ". Unmount it and remove old root-owned .work/image files before running the rootless builder.");
                }
            }
        }
    }

    private async Task CleanupAsync(CancellationToken cancellationToken)
    {
        try
        {
            var mnt = Path.Combine(_work, "mnt");
            if (await IsMountPointAsync(mnt, cancellationToken))
            {
                await RunAsync("umount", ["-R", mnt], cancellationToken, allowFailure: true);
            }

            await EnsureNoRootfsApiMountsAsync(cancellationToken);
        }
        catch
        {
            // Cleanup must not mask the original build failure.
        }
    }

    private async Task<bool> IsMountPointAsync(string path, CancellationToken cancellationToken)
    {
        var result = await _runner.RunAsync("mountpoint", ["-q", path], new CommandRunOptions(ThrowOnStartFailure: false), cancellationToken);
        return result.ExitCode == 0;
    }

    private string LogicalPath(string name)
        => Path.Combine(_work, name + ".logical");

    private long LogicalSize(string name)
        => plan.LogicalPartitions.Single(logical => logical.Name == name).SizeBytes;

    private long LogicalDataSize(string name)
        => plan.LogicalPartitions.Single(logical => logical.Name == name).DataSizeBytes;

    private long PartitionSize(string name)
        => plan.Partitions.Single(partition => partition.Name == name).SizeBytes
            ?? throw new InvalidOperationException($"{name} partition has no sizeBytes");

    private long PartitionDataSize(string name)
        => plan.Partitions.Single(partition => partition.Name == name).DataSizeBytes
            ?? throw new InvalidOperationException($"{name} partition has no dataSizeBytes");

    private static void RewriteMkinitcpioHooks(string path, IReadOnlyList<string> hooks)
    {
        var lines = File.ReadAllLines(path);
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].StartsWith("HOOKS=", StringComparison.Ordinal))
            {
                lines[i] = "HOOKS=(" + string.Join(' ', hooks) + ")";
            }
        }

        File.WriteAllLines(path, lines);
    }

    private static string KernelRelease(string rootfs)
    {
        var releases = Directory.GetDirectories(Path.Combine(rootfs, "usr", "lib", "modules"))
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Order(StringComparer.Ordinal)
            .ToArray();
        return releases.Length != 1
            ? throw new InvalidOperationException($"expected exactly one kernel modules directory, found {releases.Length}: {(releases.Length == 0 ? "none" : string.Join(' ', releases))}")
            : releases[0]!;
    }

    private static void AppendLineIfMissingPrefix(string path, string prefix, string line)
    {
        _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var lines = File.Exists(path) ? File.ReadAllLines(path) : [];
        if (!lines.Any(existing => existing.StartsWith(prefix, StringComparison.Ordinal)))
        {
            File.AppendAllText(path, line + "\n");
        }
    }

    private static void InstallFile(string source, string destination, int mode)
    {
        _ = Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(source, destination, overwrite: true);
        SetMode(destination, mode);
    }

    private static void EnsureDirectory(string path, int mode)
    {
        _ = Directory.CreateDirectory(path);
        SetMode(path, mode);
    }

    private static void SetMode(string path, int mode)
    {
        File.SetUnixFileMode(path, (UnixFileMode)Convert.ToInt32(mode.ToString("0000", CultureInfo.InvariantCulture), 8));
    }

    private static int ParseMode(string mode)
        => int.Parse(mode, NumberStyles.None, CultureInfo.InvariantCulture);

    private static string ImagePath(string rootfs, string imagePath)
        => Path.Combine(rootfs, imagePath.TrimStart('/'));

    private static void CopyDirectory(string source, string destination)
    {
        FileTreeCopier.CopyDirectory(source, destination);
    }

    private static void RemoveBootKernelArtifacts(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        foreach (var entry in Directory.EnumerateFiles(path, "vmlinuz-*"))
        {
            File.Delete(entry);
        }

        foreach (var entry in Directory.EnumerateFiles(path, "initramfs-*.img"))
        {
            File.Delete(entry);
        }
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private async Task DeleteWorkDirectoryAsync(string path, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(path) || Path.GetFullPath(path) == Path.GetPathRoot(Path.GetFullPath(path)))
        {
            throw new InvalidOperationException("refusing to remove unsafe image build work directory: " + path);
        }

        try
        {
            var result = await _rootless.RunMappedRootAsync(
                "rm",
                ["-rf", "--", path],
                new CommandRunOptions(StreamError: true),
                cancellationToken);
            _ = result.EnsureSuccess();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            throw new InvalidOperationException(
                "could not remove existing image build work directory. " +
                "A previous rootful build may have left root-owned files or stale mounts under " + path + ".",
                ex);
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException(
                "could not remove existing image build work directory. " +
                "A previous rootful build may have left root-owned files or stale mounts under " + path + ".",
                ex);
        }
    }

    private static void PrepareWritableRootfs(string rootfs)
    {
        SetMode(rootfs, 0755);
    }

    private static void Truncate(string path, long length)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
        stream.SetLength(length);
    }

    private static string Sha256Hex(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static bool FilesEqual(string left, string right)
    {
        var leftInfo = new FileInfo(left);
        var rightInfo = new FileInfo(right);
        if (leftInfo.Length != rightInfo.Length)
        {
            return false;
        }

        using var leftStream = File.OpenRead(left);
        using var rightStream = File.OpenRead(right);
        Span<byte> leftBuffer = stackalloc byte[8192];
        Span<byte> rightBuffer = stackalloc byte[8192];
        while (true)
        {
            var leftRead = leftStream.Read(leftBuffer);
            var rightRead = rightStream.Read(rightBuffer);
            if (leftRead != rightRead)
            {
                return false;
            }

            if (leftRead == 0)
            {
                return true;
            }

            if (!leftBuffer[..leftRead].SequenceEqual(rightBuffer[..rightRead]))
            {
                return false;
            }
        }
    }

    private void ValidateVersion()
    {
        if (!string.Equals(version, plan.Version, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("system image build version must match plan version");
        }
    }
}
