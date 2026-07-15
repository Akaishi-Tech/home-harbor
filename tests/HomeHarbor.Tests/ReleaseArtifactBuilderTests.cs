using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using HomeHarbor.Tooling;

namespace HomeHarbor.Tests;

[TestClass]
public sealed partial class ReleaseArtifactBuilderTests
{
    [TestMethod]
    public void Live_Installer_Erofs_Options_Avoid_The_Unsafe_Ztailpacking_Path()
    {
        var contexts = Path.Combine("relative", "file_contexts");
        var options = ReleaseArtifactBuilder.LiveInstallerErofsOptions(contexts);

        CollectionAssert.AreEqual(
            new[]
            {
                "-zlzma,109",
                "--mount-point=/",
                "--file-contexts=" + Path.GetFullPath(contexts)
            },
            options.ToArray());
        Assert.DoesNotContain("-E", options);
        Assert.DoesNotContain("ztailpacking", options);
    }

    [TestMethod]
    public void Live_Installer_Policy_Validation_Uses_Exact_System_And_Live_Erofs_Paths()
    {
        Assert.AreEqual(
            "/usr/lib/homeharbor/selinux-store/refpolicy-arch",
            ReleaseArtifactBuilder.SystemPolicyStoreErofsPath);
        Assert.AreEqual(
            "/var/lib/selinux/refpolicy-arch/active/modules",
            ReleaseArtifactBuilder.LiveInstallerPolicyModulesErofsPath);
        Assert.AreEqual(
            "/opt/homeharbor-installer/payloads",
            ReleaseArtifactBuilder.LiveInstallerPayloadErofsDirectory);
    }

    [TestMethod]
    public async Task Live_Installer_Erofs_Validation_Rejects_Changed_Policy_Store_Content()
    {
        var tempDir = CreateTempDirectory();
        try
        {
            var expected = Directory.CreateDirectory(Path.Combine(tempDir, "expected"));
            var actual = Directory.CreateDirectory(Path.Combine(tempDir, "actual"));
            _ = Directory.CreateDirectory(Path.Combine(expected.FullName, "400", "base"));
            _ = Directory.CreateDirectory(Path.Combine(actual.FullName, "400", "base"));
            await File.WriteAllTextAsync(Path.Combine(expected.FullName, "400", "base", "cil"), "BZh-good");
            await File.WriteAllTextAsync(Path.Combine(actual.FullName, "400", "base", "cil"), "BZh-good");

            await ReleaseArtifactBuilder.ValidateFileTreeContentAsync(
                expected.FullName,
                actual.FullName,
                "test policy store");

            await File.WriteAllTextAsync(Path.Combine(actual.FullName, "400", "base", "cil"), "\0BZh-bad");
            var changed = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                ReleaseArtifactBuilder.ValidateFileTreeContentAsync(
                    expected.FullName,
                    actual.FullName,
                    "test policy store"));
            Assert.Contains("400/base/cil", changed.Message);

            await File.WriteAllTextAsync(Path.Combine(actual.FullName, "400", "base", "cil"), "BZh-good");
            await File.WriteAllTextAsync(Path.Combine(actual.FullName, "400", "base", "unexpected"), "extra");
            var fileSet = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                ReleaseArtifactBuilder.ValidateFileTreeContentAsync(
                    expected.FullName,
                    actual.FullName,
                    "test policy store"));
            Assert.Contains("unexpected=[400/base/unexpected]", fileSet.Message);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task Live_Installer_Erofs_Validation_Rejects_Metadata_And_Entry_Type_Changes()
    {
        var tempDir = CreateTempDirectory();
        try
        {
            var expected = Directory.CreateDirectory(Path.Combine(tempDir, "expected"));
            var actual = Directory.CreateDirectory(Path.Combine(tempDir, "actual"));
            File.SetUnixFileMode(expected.FullName, (UnixFileMode)Convert.ToInt32("700", 8));
            File.SetUnixFileMode(actual.FullName, (UnixFileMode)Convert.ToInt32("700", 8));

            var expectedEmpty = Directory.CreateDirectory(Path.Combine(expected.FullName, "disabled"));
            File.SetUnixFileMode(expectedEmpty.FullName, (UnixFileMode)Convert.ToInt32("700", 8));
            var missingDirectory = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                ReleaseArtifactBuilder.ValidateFileTreeContentAsync(
                    expected.FullName,
                    actual.FullName,
                    "test policy store"));
            Assert.Contains("missing=[disabled]", missingDirectory.Message);

            var actualEmpty = Directory.CreateDirectory(Path.Combine(actual.FullName, "disabled"));
            File.SetUnixFileMode(actualEmpty.FullName, (UnixFileMode)Convert.ToInt32("700", 8));
            await ReleaseArtifactBuilder.ValidateFileTreeContentAsync(
                expected.FullName,
                actual.FullName,
                "test policy store");

            Directory.Delete(actualEmpty.FullName);
            await File.WriteAllTextAsync(actualEmpty.FullName, string.Empty);
            File.SetUnixFileMode(actualEmpty.FullName, (UnixFileMode)Convert.ToInt32("700", 8));
            var changedType = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                ReleaseArtifactBuilder.ValidateFileTreeContentAsync(
                    expected.FullName,
                    actual.FullName,
                    "test policy store"));
            Assert.Contains("entry changed inside EROFS: disabled", changedType.Message);

            File.Delete(actualEmpty.FullName);
            actualEmpty = Directory.CreateDirectory(actualEmpty.FullName);
            File.SetUnixFileMode(actualEmpty.FullName, (UnixFileMode)Convert.ToInt32("755", 8));
            var changedMode = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                ReleaseArtifactBuilder.ValidateFileTreeContentAsync(
                    expected.FullName,
                    actual.FullName,
                    "test policy store"));
            Assert.Contains("entry changed inside EROFS: disabled", changedMode.Message);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task Live_Installer_Erofs_Validation_Rejects_Changed_Symbolic_Link_Target_Without_Traversal()
    {
        var tempDir = CreateTempDirectory();
        try
        {
            var expected = Directory.CreateDirectory(Path.Combine(tempDir, "expected"));
            var actual = Directory.CreateDirectory(Path.Combine(tempDir, "actual"));
            foreach (var root in new[] { expected.FullName, actual.FullName })
            {
                _ = Directory.CreateDirectory(Path.Combine(root, "target-a"));
                _ = Directory.CreateDirectory(Path.Combine(root, "target-b"));
            }

            _ = Directory.CreateSymbolicLink(Path.Combine(expected.FullName, "selected"), "target-a");
            _ = Directory.CreateSymbolicLink(Path.Combine(actual.FullName, "selected"), "target-b");
            var changedLink = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                ReleaseArtifactBuilder.ValidateFileTreeContentAsync(
                    expected.FullName,
                    actual.FullName,
                    "test policy store"));
            Assert.Contains("entry changed inside EROFS: selected", changedLink.Message);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task Live_Installer_Erofs_Validation_Rejects_Fifo_Without_Blocking()
    {
        var tempDir = CreateTempDirectory();
        try
        {
            var expected = Directory.CreateDirectory(Path.Combine(tempDir, "expected"));
            var actual = Directory.CreateDirectory(Path.Combine(tempDir, "actual"));
            await File.WriteAllTextAsync(Path.Combine(expected.FullName, "cil"), "regular");
            var fifo = Path.Combine(actual.FullName, "cil");
            var startInfo = new ProcessStartInfo("/usr/bin/mkfifo")
            {
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add(fifo);
            using (var process = Process.Start(startInfo)
                                 ?? throw new InvalidOperationException("could not start mkfifo"))
            {
                await process.WaitForExitAsync();
                Assert.AreEqual(0, process.ExitCode);
            }

            var validation = Task.Run(() => ReleaseArtifactBuilder.ValidateFileTreeContentAsync(
                expected.FullName,
                actual.FullName,
                "test policy store"));
            var completed = await Task.WhenAny(validation, Task.Delay(TimeSpan.FromSeconds(2)));
            Assert.AreSame(validation, completed, "FIFO validation blocked instead of rejecting the inode type");
            var unsupported = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => validation);
            Assert.Contains("unsupported file tree entry type Fifo", unsupported.Message);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public void RequireCompleteLogicalPairArtifact_Accepts_Only_Exact_Identical_AB_Images()
    {
        var tempDir = CreateTempDirectory();
        try
        {
            const int expectedBytes = 4096;
            foreach (var logicalName in new[] { "root", "modules", "firmware" })
            {
                var payload = Enumerable.Range(0, expectedBytes)
                    .Select(index => (byte)(index % 251))
                    .ToArray();
                var imageA = Path.Combine(tempDir, logicalName + "_a.logical");
                var imageB = Path.Combine(tempDir, logicalName + "_b.logical");
                File.WriteAllBytes(imageA, payload);
                File.WriteAllBytes(imageB, payload);

                Assert.AreEqual(
                    imageA,
                    ReleaseArtifactBuilder.RequireCompleteLogicalPairArtifact(
                        tempDir,
                        logicalName,
                        expectedBytes));

                payload[^1] ^= 0xFF;
                File.WriteAllBytes(imageB, payload);
                _ = Assert.ThrowsExactly<InvalidOperationException>(() =>
                    ReleaseArtifactBuilder.RequireCompleteLogicalPairArtifact(
                        tempDir,
                        logicalName,
                        expectedBytes));

                File.WriteAllBytes(imageA, new byte[expectedBytes - 1]);
                File.WriteAllBytes(imageB, new byte[expectedBytes - 1]);
                _ = Assert.ThrowsExactly<InvalidOperationException>(() =>
                    ReleaseArtifactBuilder.RequireCompleteLogicalPairArtifact(
                        tempDir,
                        logicalName,
                        expectedBytes));

                File.WriteAllBytes(imageA, new byte[expectedBytes + 1]);
                File.WriteAllBytes(imageB, new byte[expectedBytes + 1]);
                _ = Assert.ThrowsExactly<InvalidOperationException>(() =>
                    ReleaseArtifactBuilder.RequireCompleteLogicalPairArtifact(
                        tempDir,
                        logicalName,
                        expectedBytes));
            }
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public void System_And_Kernel_Ota_Bundles_Use_Complete_Logical_Partition_Images()
    {
        var root = RepositoryRoot();
        var releaseSource = File.ReadAllText(Path.Combine(
            root,
            "tools",
            "system-build",
            "src",
            "HomeHarbor.SystemBuild",
            "ReleaseArtifactBuilder.cs"));
        var compact = MyRegex().Replace(releaseSource, " ");

        foreach (var (logicalName, payloadName, manifestHash) in new[]
                 {
                     ("root", "rootfs.img", "rootfsHash"),
                     ("modules", "modules.img", "modulesHash"),
                     ("firmware", "firmware.img", "firmwareHash")
                 })
        {
            var assignment = Regex.Match(
                compact,
                "var (?<source>[A-Za-z][A-Za-z0-9]*) = RequireCompleteLogicalPairArtifact\\(_imageWork, \\\"" +
                logicalName +
                "\\\"",
                RegexOptions.CultureInvariant);
            Assert.IsTrue(assignment.Success, "complete logical image was not selected for " + logicalName);
            var source = assignment.Groups["source"].Value;
            Assert.Contains(
                $"CopyReleaseFileAsync({source}, Path.Combine(root, \"{payloadName}\")",
                compact);
            Assert.Contains(
                $"CopyShaAsync({source}, Path.Combine(root, \"{payloadName}.sha256\")",
                compact);
            Assert.Contains(
                $"[\"{manifestHash}\"] = await Sha256HexAsync({source},",
                compact);
        }

        Assert.DoesNotContain(
            "CopyReleaseFileAsync(plan.Artifacts.Rootfs.Path, Path.Combine(root, \"rootfs.img\")",
            compact);
        Assert.DoesNotContain(
            "CopyReleaseFileAsync(plan.Artifacts.Modules.Path, Path.Combine(root, \"modules.img\")",
            compact);
        Assert.DoesNotContain(
            "CopyReleaseFileAsync(plan.Artifacts.Firmware.Path, Path.Combine(root, \"firmware.img\")",
            compact);
        Assert.DoesNotContain(
            "Sha256HexAsync(plan.Artifacts.Rootfs.Path",
            compact);
        Assert.DoesNotContain(
            "Sha256HexAsync(plan.Artifacts.Modules.Path",
            compact);
        Assert.DoesNotContain(
            "Sha256HexAsync(plan.Artifacts.Firmware.Path",
            compact);
        Assert.Contains(
            "var expectedSystemRootfs = RequireCompleteLogicalPairArtifact(_imageWork, \"root\", rootPartitionBytes)",
            compact);
        Assert.Contains(
            "ExtractSystemOtaRootfsAsync( embeddedSystemOta, embeddedSystemRootfs, rootPartitionBytes",
            compact);
        Assert.Contains(
            "RequireSameFileContentAsync( expectedSystemRootfs, embeddedSystemRootfs",
            compact);
        Assert.Contains(
            "\"--path=\" + SystemPolicyStoreErofsPath, embeddedSystemRootfs",
            compact);
        Assert.DoesNotContain(
            "SystemPolicyStoreErofsPath, plan.Artifacts.Rootfs.Path",
            compact);
    }

    [TestMethod]
    public void OtaApply_Requires_Exact_Partition_Size_For_Root_Modules_And_Firmware()
    {
        var otaSource = File.ReadAllText(Path.Combine(
            RepositoryRoot(),
            "tools",
            "system-build",
            "external",
            "system-utils",
            "src",
            "HomeHarbor.Tooling",
            "OtaApplyCommand.cs"));

        Assert.IsTrue(RootPartitionWriteRegex().IsMatch(otaSource));
        Assert.IsTrue(ModulesPartitionWriteRegex().IsMatch(otaSource));
        Assert.IsTrue(FirmwarePartitionWriteRegex().IsMatch(otaSource));
        Assert.Contains(
            "new FileInfo(image).Length != deviceSize",
            otaSource);

        Assert.DoesNotContain(
            "RunRequiredAsync(runner, \"dd\", [\"if=\" + plan.RootfsPath",
            otaSource);
        Assert.DoesNotContain(
            "RunRequiredAsync(runner, \"dd\", [\"if=\" + Path.Combine(bundleRoot, \"modules.img\")",
            otaSource);
        Assert.DoesNotContain(
            "RunRequiredAsync(runner, \"dd\", [\"if=\" + Path.Combine(bundleRoot, \"firmware.img\")",
            otaSource);
    }

    [TestMethod]
    public void OrderOtaBundleEntries_Puts_Signed_Manifest_Before_Large_Payloads()
    {
        var tempDir = Directory.CreateTempSubdirectory("homeharbor-ota-order-");
        try
        {
            var top = Path.Combine(tempDir.FullName, "homeharbor-kernel-generic-ota-test");
            _ = Directory.CreateDirectory(top);
            File.WriteAllText(Path.Combine(top, "boot.efi"), "boot payload");
            File.WriteAllText(Path.Combine(top, "manifest.json"), "{}");
            File.WriteAllText(Path.Combine(top, "modules.img"), "modules payload");

            var entries = ReleaseArtifactBuilder.OrderOtaBundleEntries(
                tempDir.FullName,
                Path.GetFileName(top));

            Assert.AreEqual("manifest.json", Path.GetFileName(entries[0]));
            CollectionAssert.AreEquivalent(
                new[] { "boot.efi", "manifest.json", "modules.img" },
                entries.Select(Path.GetFileName).ToArray());
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [TestMethod]
    public async Task CopyOptionalReleaseFileWithSha_Copies_Payload_And_Hash_Sidecar()
    {
        var tempDir = CreateTempDirectory();
        try
        {
            var source = Path.Combine(tempDir, "homeharbor-bootloader.efi");
            var destination = Path.Combine(tempDir, "ota", "HomeHarborBoot.efi");
            var shaDestination = Path.Combine(tempDir, "ota", "HomeHarborBoot.efi.sha256");
            const string payload = "bootloader payload";
            await File.WriteAllTextAsync(source, payload);

            await ReleaseArtifactBuilder.CopyOptionalReleaseFileWithShaAsync(
                source,
                destination,
                shaDestination,
                CancellationToken.None);

            Assert.AreEqual(payload, await File.ReadAllTextAsync(destination));
            var expectedHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
            Assert.AreEqual(expectedHash + "  " + Path.GetFileName(source) + "\n", await File.ReadAllTextAsync(shaDestination));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task CopyOptionalReleaseFileWithSha_Skips_Missing_Optional_Payload()
    {
        var tempDir = CreateTempDirectory();
        try
        {
            var source = Path.Combine(tempDir, "missing.efi");
            var destination = Path.Combine(tempDir, "ota", "HomeHarborBoot.efi");
            var shaDestination = Path.Combine(tempDir, "ota", "HomeHarborBoot.efi.sha256");

            await ReleaseArtifactBuilder.CopyOptionalReleaseFileWithShaAsync(
                source,
                destination,
                shaDestination,
                CancellationToken.None);

            Assert.IsFalse(File.Exists(destination));
            Assert.IsFalse(File.Exists(shaDestination));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task InstallLiveInstallerTrustAnchor_Copies_Key_To_Trusted_Iso_Path()
    {
        var tempDir = CreateTempDirectory();
        try
        {
            var profile = Path.Combine(tempDir, "iso-profile");
            var publicKey = Path.Combine(tempDir, "release.pub.pem");
            const string key = "-----BEGIN PUBLIC KEY-----\ninstaller-trust-anchor\n-----END PUBLIC KEY-----\n";
            await File.WriteAllTextAsync(publicKey, key);

            await ReleaseArtifactBuilder.InstallLiveInstallerTrustAnchorAsync(
                profile,
                publicKey,
                CancellationToken.None);

            var installed = Path.Combine(profile, "airootfs", "etc", "homeharbor", "release.pub.pem");
            Assert.IsTrue(File.Exists(installed));
            Assert.AreEqual(key, await File.ReadAllTextAsync(installed));
            if (!OperatingSystem.IsWindows())
            {
                Assert.AreEqual(
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead,
                    File.GetUnixFileMode(installed) & (UnixFileMode)0x1FF);
            }
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public void LiveInstaller_Package_List_Uses_SELinux_Variants_With_Upstream_Archiso_Tooling()
    {
        var recoveryPackages = SystemImageBuildDescriptor
            .LoadDefaultPlan(RepositoryRoot(), "1.0.0")
            .Packages
            .Recovery;
        var baselinePackages = new[]
        {
            "base",
            "cloud-init",
            "hyperv",
            "linux",
            "mkinitcpio",
            "mkinitcpio-archiso",
            "open-vm-tools",
            "openssh",
            "pv",
            "qemu-guest-agent",
            "syslinux",
            "virtualbox-guest-utils-nox"
        };

        var packages = ReleaseArtifactBuilder.BuildLiveInstallerPackageList(
            baselinePackages,
            recoveryPackages);

        CollectionAssert.AreEquivalent(
            new[]
            {
                "android-tools",
                "audit",
                "base",
                "checkpolicy",
                "cloud-init",
                "coreutils-selinux",
                "dbus-broker-selinux",
                "dbus-broker-units",
                "dbus-selinux",
                "device-mapper-selinux",
                "dosfstools",
                "dotnet-runtime",
                "erofs-utils-selinux",
                "findutils-selinux",
                "gpm",
                "gptfdisk",
                "homeharbor-installer",
                "homeharbor-selinux-policy",
                "hyperv",
                "iproute2-selinux",
                "jq",
                "libnm-selinux",
                "libselinux",
                "libsemanage",
                "libsepol",
                "linux",
                "mkinitcpio",
                "mkinitcpio-archiso",
                "networkmanager-selinux",
                "open-vm-tools",
                "openssh-selinux",
                "openssl",
                "pam-selinux",
                "pambase-selinux",
                "policycoreutils",
                "psmisc-selinux",
                "pv",
                "qemu-guest-agent",
                "secilc",
                "selinux-refpolicy-arch",
                "semodule-utils",
                "shadow-selinux",
                "sudo-selinux",
                "syslinux",
                "systemd-libs-selinux",
                "systemd-resolvconf-selinux",
                "systemd-selinux",
                "systemd-sysvcompat-selinux",
                "util-linux-libs-selinux",
                "util-linux-selinux",
                "virtualbox-guest-utils-nox"
            },
            packages.ToArray());
        ReleaseArtifactBuilder.ValidateLiveInstallerPackageList(
            string.Join('\n', packages.Select(package => package + " 1-1")));

        _ = Assert.ThrowsExactly<InvalidOperationException>(() =>
            ReleaseArtifactBuilder.ValidateLiveInstallerPackageList(
                string.Join('\n', packages.Select(package => package + " 1-1")) + "\nsystemd 261.1-1\n"));
        _ = Assert.ThrowsExactly<InvalidOperationException>(() =>
            ReleaseArtifactBuilder.ValidateLiveInstallerPackageList(
                string.Join(
                    '\n',
                    packages
                        .Where(package => package != "homeharbor-selinux-policy")
                        .Select(package => package + " 1-1"))));
    }

    [TestMethod]
    public void LiveInstaller_Boot_Configurations_Enable_SELinux_Without_Changing_Memtest()
    {
        const string grub = """
            menuentry 'HomeHarbor' {
                linux /hh/boot/x86_64/vmlinuz-linux archisobasedir=hh
            }
            menuentry 'Memtest' {
                linux /boot/memtest86+/memtest.efi
            }
            """;
        var configuredGrub = ReleaseArtifactBuilder.AddLiveInstallerSelinuxKernelArguments(grub);
        Assert.Contains(
            "linux /hh/boot/x86_64/vmlinuz-linux archisobasedir=hh " + SecureBootAssets.SelinuxArgs,
            configuredGrub);
        Assert.DoesNotContain(
            "linux /boot/memtest86+/memtest.efi " + SecureBootAssets.SelinuxArgs,
            configuredGrub);
        Assert.AreEqual(
            configuredGrub,
            ReleaseArtifactBuilder.AddLiveInstallerSelinuxKernelArguments(configuredGrub));
        ReleaseArtifactBuilder.ValidateLiveInstallerBootConfiguration("grub.cfg", configuredGrub);

        var conflictingGrub = configuredGrub.Replace(
            "enforcing=1",
            "enforcing=1 enforcing=0",
            StringComparison.Ordinal);
        _ = Assert.ThrowsExactly<InvalidOperationException>(() =>
            ReleaseArtifactBuilder.ValidateLiveInstallerBootConfiguration("grub.cfg", conflictingGrub));
        var normalizedGrub = ReleaseArtifactBuilder.AddLiveInstallerSelinuxKernelArguments(conflictingGrub);
        Assert.DoesNotContain("enforcing=0", normalizedGrub, StringComparison.Ordinal);
        ReleaseArtifactBuilder.ValidateLiveInstallerBootConfiguration("grub.cfg", normalizedGrub);

        const string syslinux = """
            LABEL homeharbor
            LINUX /hh/boot/x86_64/vmlinuz-linux
            INITRD /hh/boot/x86_64/initramfs-linux.img
            APPEND archisobasedir=hh
            """;
        var configuredSyslinux = ReleaseArtifactBuilder.AddLiveInstallerSelinuxKernelArguments(syslinux);
        Assert.Contains("APPEND archisobasedir=hh " + SecureBootAssets.SelinuxArgs, configuredSyslinux);
        ReleaseArtifactBuilder.ValidateLiveInstallerBootConfiguration("syslinux-linux.cfg", configuredSyslinux);

        _ = Assert.ThrowsExactly<InvalidOperationException>(() =>
            ReleaseArtifactBuilder.ValidateLiveInstallerBootConfiguration("grub.cfg", grub));
    }

    [TestMethod]
    public async Task Release_Copies_Only_A_Verified_Local_Package_Set_With_Its_Provenance()
    {
        var tempDir = CreateTempDirectory();
        try
        {
            var recipe = Path.Combine(tempDir, "packaging", "arch", "selinux", "policy");
            _ = Directory.CreateDirectory(recipe);
            await File.WriteAllTextAsync(Path.Combine(recipe, "PKGBUILD"), "pkgname=policy\n");
            var source = Path.Combine(tempDir, "source");
            var destination = Path.Combine(tempDir, "destination");
            _ = Directory.CreateDirectory(source);
            var archive = Path.Combine(source, "policy-1-1-any.pkg.tar.zst");
            await File.WriteAllTextAsync(archive, "locally built package");
            await ArchPackageSetProvenance.WriteAsync(tempDir, "1.0.0", source);

            await ReleaseArtifactBuilder.CopyVerifiedPackageSetAsync(
                tempDir,
                "1.0.0",
                source,
                destination,
                CancellationToken.None);

            Assert.IsTrue(File.Exists(Path.Combine(destination, Path.GetFileName(archive))));
            Assert.IsTrue(File.Exists(Path.Combine(destination, ArchPackageSetProvenance.FileName)));
            await ArchPackageSetProvenance.VerifyAsync(tempDir, "1.0.0", destination);

            await File.AppendAllTextAsync(archive, "tampered");
            _ = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                ReleaseArtifactBuilder.CopyVerifiedPackageSetAsync(
                    tempDir,
                    "1.0.0",
                    source,
                    Path.Combine(tempDir, "rejected"),
                    CancellationToken.None));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    [OSCondition(ConditionMode.Exclude, OperatingSystems.Windows)]
    public async Task DeleteManagedReleaseWorkDirectory_Uses_Constrained_Privileged_Fallback()
    {
        var tempDir = CreateTempDirectory();
        try
        {
            var work = Path.Combine(tempDir, ".work", "release", "1.0.0", "mkarchiso");
            _ = Directory.CreateDirectory(work);
            await File.WriteAllTextAsync(Path.Combine(work, "root-owned-marker"), "marker");
            File.SetUnixFileMode(work, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            var runner = new CleanupRunner(work);

            await ReleaseArtifactBuilder.DeleteManagedReleaseWorkDirectoryAsync(
                tempDir,
                work,
                runner,
                CancellationToken.None);

            Assert.IsFalse(Directory.Exists(work));
            Assert.IsTrue(runner.WasCalled);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                File.SetUnixFileMode(tempDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task DeleteManagedReleaseWorkDirectory_Rejects_Path_Outside_Managed_Root()
    {
        var tempDir = CreateTempDirectory();
        try
        {
            var outside = Path.Combine(tempDir, "outside");
            _ = Directory.CreateDirectory(outside);

            _ = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                ReleaseArtifactBuilder.DeleteManagedReleaseWorkDirectoryAsync(
                    tempDir,
                    outside,
                    new CleanupRunner(outside),
                    CancellationToken.None));

            Assert.IsTrue(Directory.Exists(outside));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    private sealed class CleanupRunner(string expectedPath) : ICommandRunner
    {
        public bool WasCalled { get; private set; }

        public Task<CommandResult> RunAsync(
            string fileName,
            IEnumerable<string> arguments,
            CommandRunOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var args = arguments.ToArray();
            Assert.AreEqual("sudo", fileName);
            CollectionAssert.AreEqual(new[] { "-n", "rm", "-rf", "--", expectedPath }, args);
            WasCalled = true;
            File.SetUnixFileMode(
                expectedPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Directory.Delete(expectedPath, recursive: true);
            return Task.FromResult(new CommandResult(0, string.Empty, string.Empty, "sudo rm"));
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "homeharbor-release-builder-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(path);
        return path;
    }

    private static string RepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "HomeHarbor.slnx")))
            {
                return current.FullName;
            }
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Repository root not found.");
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex MyRegex();

    [GeneratedRegex(
        @"WriteCompletePartitionImageAsync\(\s*runner,\s*plan\.RootfsPath,\s*rootDevice,",
        RegexOptions.CultureInvariant)]
    private static partial Regex RootPartitionWriteRegex();

    [GeneratedRegex(
        @"WriteCompletePartitionImageAsync\(\s*runner,\s*Path\.Combine\(bundleRoot, \""modules\.img\""\),\s*modulesDevice,",
        RegexOptions.CultureInvariant)]
    private static partial Regex ModulesPartitionWriteRegex();

    [GeneratedRegex(
        @"WriteCompletePartitionImageAsync\(\s*runner,\s*Path\.Combine\(bundleRoot, \""firmware\.img\""\),\s*firmwareDevice,",
        RegexOptions.CultureInvariant)]
    private static partial Regex FirmwarePartitionWriteRegex();
}
