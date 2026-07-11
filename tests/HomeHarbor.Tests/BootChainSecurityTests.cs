using System.Security.Cryptography;
using HomeHarbor.Tooling;

namespace HomeHarbor.Tests;

[TestClass]
public sealed class BootChainSecurityTests
{
    [TestMethod]
    public void Selector_Avb_Key_Validation_Accepts_Only_Supported_Exact_Encodings()
    {
        Assert.AreEqual(2048, BuildToolCommands.RequireSelectorSupportedAvbPublicKey(EncodedAvbKey(2048)));
        Assert.AreEqual(4096, BuildToolCommands.RequireSelectorSupportedAvbPublicKey(EncodedAvbKey(4096)));
        _ = Assert.ThrowsExactly<InvalidOperationException>(() =>
            BuildToolCommands.RequireSelectorSupportedAvbPublicKey(EncodedAvbKey(8192)));
        _ = Assert.ThrowsExactly<InvalidOperationException>(() =>
            BuildToolCommands.RequireSelectorSupportedAvbPublicKey(new byte[519]));
    }

    [TestMethod]
    public void Avb_Signing_Algorithm_Must_Match_A_Selector_Supported_Key()
    {
        var temp = Path.Combine(Path.GetTempPath(), "homeharbor-avb-key-" + Guid.NewGuid().ToString("N") + ".pem");
        try
        {
            using var rsa = RSA.Create(2048);
            File.WriteAllText(temp, rsa.ExportPkcs8PrivateKeyPem());
            Assert.AreEqual(
                "SHA256_RSA2048",
                BuildKeyDefaults.RequireSupportedAvbSigningAlgorithm(temp, "SHA256_RSA2048"));
            _ = Assert.ThrowsExactly<InvalidOperationException>(() =>
                BuildKeyDefaults.RequireSupportedAvbSigningAlgorithm(temp, "SHA256_RSA4096"));
        }
        finally
        {
            File.Delete(temp);
        }
    }

    [TestMethod]
    public void Selector_Build_Refreshes_The_Environment_Selected_Avb_Key_Atomically()
    {
        var makefile = File.ReadAllText(Path.Combine(RepositoryRoot(), "boot", "bootloader", "Makefile"));

        Assert.Contains("$(AVB_PUBLIC_KEY_HEADER): FORCE", makefile);
        Assert.Contains("generate-efi-avb-public-key \"$$tmp\"", makefile);
        Assert.Contains("cmp -s \"$$tmp\" \"$@\"", makefile);
        Assert.Contains("mv -f \"$$tmp\" \"$@\"", makefile);
    }

    [TestMethod]
    public void Ota_Default_Slot_Is_Committed_Only_After_Health_Succeeds()
    {
        var root = RepositoryRoot();
        var program = File.ReadAllText(Path.Combine(root, "src", "HomeHarbor.Agent", "Program.cs"));
        var commitUnit = File.ReadAllText(Path.Combine(root, "os", "systemd", "homeharbor-ota-commit.service"));
        var manifest = File.ReadAllText(Path.Combine(root, "system", "x86_64", "system", "manifest.yml"));

        var healthIndex = program.IndexOf("await http.GetStringAsync(healthUrl", StringComparison.Ordinal);
        var commitIndex = program.IndexOf("await OtaCommitAsync(", healthIndex, StringComparison.Ordinal);
        Assert.IsGreaterThanOrEqualTo(0, healthIndex);
        Assert.IsGreaterThan(healthIndex, commitIndex);
        Assert.Contains("Requires=homeharbor-boot-success.service", commitUnit);
        Assert.Contains("After=homeharbor-boot-success.service", commitUnit);
        Assert.Contains("- homeharbor-boot-success", manifest);
        Assert.DoesNotContain("- homeharbor-ota-commit", manifest);
    }

    [TestMethod]
    public void Verified_Boot_Environment_Is_Published_From_A_Root_Only_Runtime_Directory()
    {
        var root = RepositoryRoot();
        var init = File.ReadAllText(Path.Combine(root, "boot", "init", "homeharbor-verity.c"));
        var bootSuccess = File.ReadAllText(Path.Combine(root, "src", "HomeHarbor.Agent", "BootSuccessCommand.cs"));
        var apiUnit = File.ReadAllText(Path.Combine(root, "os", "systemd", "homeharbor-api.service"));

        Assert.Contains("#define BOOT_RUN_DIR \"/run/homeharbor-boot\"", init);
        Assert.Contains("O_NOFOLLOW", init);
        Assert.Contains("directory.st_uid != 0", init);
        Assert.Contains("MS_BIND | MS_REMOUNT | MS_RDONLY", init);
        Assert.Contains("/run/homeharbor-boot/boot.env", bootSuccess);
        Assert.DoesNotContain("/run/homeharbor-boot", apiUnit);
    }

    [TestMethod]
    public void MaskSystemdUnit_Disables_SystemdBoot_Counting_Helper_In_Immutable_Rootfs()
    {
        var temp = Directory.CreateTempSubdirectory("homeharbor-unit-mask-");
        try
        {
            SystemImageBuilder.MaskSystemdUnit(temp.FullName, "systemd-bless-boot.service");

            var path = Path.Combine(temp.FullName, "etc", "systemd", "system", "systemd-bless-boot.service");
            Assert.AreEqual("/dev/null", new FileInfo(path).LinkTarget);
            _ = Assert.ThrowsExactly<InvalidOperationException>(() =>
                SystemImageBuilder.MaskSystemdUnit(temp.FullName, "../outside.service"));
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }

    [TestMethod]
    public void Initramfs_Usr_Overlay_Never_Uses_Unauthenticated_Data_Volume_Content()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot(), "boot", "init", "homeharbor-verity.c"));

        Assert.DoesNotContain("append_system_app_usr_lowerdirs", source);
        Assert.DoesNotContain("/new_root/homeharbor-data/system-apps", source);
        Assert.Contains("char *lowerdirs = mount_kernel_addons();", source);
        Assert.Contains("validate_addons();", source);
    }

    [TestMethod]
    public void Recovery_Uses_Signed_Generic_Raw_Uki_And_Signed_Verity_Geometry()
    {
        var root = RepositoryRoot();
        var selector = File.ReadAllText(Path.Combine(root, "boot", "bootloader", "HomeHarborBoot.c"));
        var init = File.ReadAllText(Path.Combine(root, "boot", "init", "homeharbor-verity.c"));
        var secureBoot = File.ReadAllText(Path.Combine(root, "src", "HomeHarbor.Tooling", "SecureBootAssets.cs"));

        Assert.Contains("label = L\"boot_a\"", selector);
        Assert.Contains("label = L\"boot_b\"", selector);
        Assert.DoesNotContain("boot_recovery_erofs_partition(ImageHandle", selector);
        Assert.Contains("boot_current_requests_recovery()", init);
        Assert.Contains("open_recovery_verity();", init);
        Assert.Contains("homeharbor.recovery_a_verity", init);
        Assert.Contains("homeharbor.recovery_b_verity", init);
        Assert.Contains("homeharbor.boot_generic=1", secureBoot);
    }

    [TestMethod]
    public void Generic_Boot_Binds_Selected_Root_Vbmeta_Digest_Into_Initramfs()
    {
        var root = RepositoryRoot();
        var selector = File.ReadAllText(Path.Combine(root, "boot", "bootloader", "HomeHarborBoot.c"));
        var variables = File.ReadAllText(Path.Combine(root, "boot", "bootloader", "variables.c"));
        var avb = File.ReadAllText(Path.Combine(root, "boot", "bootloader", "avb.c"));
        var init = File.ReadAllText(Path.Combine(root, "boot", "init", "homeharbor-verity.c"));

        Assert.Contains("if (root_slot[0] == 'B')", selector);
        Assert.Contains("verify_vbmeta_signature_preflight(vbmeta_label, secure_boot_active, vbmeta_digest)", selector);
        Assert.Contains("write_efi_boot_current(slot, root_slot, mode, recovery_slot, vbmeta_digest)", selector);
        Assert.Contains("refusing to boot without a fresh vbmeta digest handoff", selector);
        Assert.Contains("calculate_vbmeta_digest", avb);
        Assert.Contains("if (secure_boot_active)", avb);
        Assert.Contains("refusing to boot with Secure Boot enabled and invalid vbmeta", avb);
        Assert.Contains("payload[i++] = ':';", variables);
        Assert.Contains("set_string(&state.vbmeta_digest, current.vbmeta_digest);", init);
        Assert.Contains("validate_sha256(\"vbmeta\", state.vbmeta_digest);", init);
        Assert.Contains("recovery vbmeta", init);
    }

    [TestMethod]
    public void Recovery_Release_Uses_Complete_Verified_Partition_Artifact()
    {
        var temp = Directory.CreateTempSubdirectory("homeharbor-recovery-artifact-");
        try
        {
            const int expectedBytes = 4096;
            var recoveryA = Path.Combine(temp.FullName, "recovery_a.logical");
            var recoveryB = Path.Combine(temp.FullName, "recovery_b.logical");
            File.WriteAllBytes(recoveryA, new byte[expectedBytes]);
            File.Copy(recoveryA, recoveryB);

            Assert.AreEqual(
                recoveryA,
                ReleaseArtifactBuilder.RequireCompleteRecoveryPartitionArtifact(temp.FullName, expectedBytes));

            using (var stream = new FileStream(recoveryB, FileMode.Open, FileAccess.Write, FileShare.None))
            {
                stream.Position = expectedBytes - 1;
                stream.WriteByte(1);
            }
            _ = Assert.ThrowsExactly<InvalidOperationException>(() =>
                ReleaseArtifactBuilder.RequireCompleteRecoveryPartitionArtifact(temp.FullName, expectedBytes));
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }

    [TestMethod]
    public void Recovery_Build_And_Ota_Validate_And_Write_The_Complete_Image()
    {
        var root = RepositoryRoot();
        var imageBuilder = File.ReadAllText(Path.Combine(root, "src", "HomeHarbor.Tooling", "SystemImageBuilder.cs"));
        var releaseBuilder = File.ReadAllText(Path.Combine(root, "src", "HomeHarbor.Tooling", "ReleaseArtifactBuilder.cs"));
        var ota = File.ReadAllText(Path.Combine(root, "src", "HomeHarbor.Agent", "OtaApplyCommand.cs"));
        var installer = File.ReadAllText(Path.Combine(root, "src", "HomeHarbor.Installer", "InstallDiskCommand.cs"));

        Assert.Contains("VerifyCompleteVerityImageAsync(LogicalPath(\"recovery_a\")", imageBuilder);
        Assert.Contains("\"veritysetup\"", imageBuilder);
        Assert.Contains("RequireCompleteRecoveryPartitionArtifact(_imageWork", releaseBuilder);
        Assert.Contains("WriteCompletePartitionImageAsync", ota);
        Assert.Contains("image size does not exactly match its target partition", ota);
        Assert.Contains("PrepareCompleteRecoveryLogicalPair", installer);
        Assert.Contains("must be exactly", installer);
        Assert.Contains("PrepareCompleteAvbLogicalPairAsync", installer);
        Assert.Contains("VerifyCompleteVerityImageAsync", installer);
        Assert.Contains("\"veritysetup\"", installer);
        Assert.DoesNotContain("add_hashtree_footer", installer);
    }

    [TestMethod]
    public void Zfs_Is_Reported_Unavailable_Until_Addon_Metadata_Is_Signed()
    {
        var service = File.ReadAllText(Path.Combine(
            RepositoryRoot(),
            "src",
            "HomeHarbor.Api",
            "Services",
            "StorageOobeService.cs"));

        Assert.Contains("Available: false", service);
        Assert.Contains("cryptographically bound into the signed boot UKI", service);
    }

    [TestMethod]
    public void Installer_Never_Uses_Fleet_Private_Signing_Keys()
    {
        var root = RepositoryRoot();
        var installer = File.ReadAllText(Path.Combine(root, "src", "HomeHarbor.Installer", "InstallDiskCommand.cs"));
        var release = File.ReadAllText(Path.Combine(root, "src", "HomeHarbor.Tooling", "ReleaseArtifactBuilder.cs"));

        Assert.DoesNotContain("HOMEHARBOR_SECURE_BOOT_KEY", installer);
        Assert.DoesNotContain("sbsign", installer);
        Assert.DoesNotContain("sbvarsign", installer);
        Assert.DoesNotContain("sbsiglist", installer);
        Assert.Contains("the installer never builds or signs boot code locally", installer);
        Assert.Contains("CopyReleaseFileAsync(plan.Artifacts.Bootloader.Path", release);
        Assert.Contains("[\"bootloaderHash\"]", release);
        Assert.Contains("[\"fallbackBootHash\"]", release);
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

    private static byte[] EncodedAvbKey(int bits)
    {
        var data = new byte[8 + bits / 8 * 2];
        data[0] = (byte)(bits >> 24);
        data[1] = (byte)(bits >> 16);
        data[2] = (byte)(bits >> 8);
        data[3] = (byte)bits;
        return data;
    }
}
