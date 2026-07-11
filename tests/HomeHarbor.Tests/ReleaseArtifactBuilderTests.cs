using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using HomeHarbor.Tooling;

namespace HomeHarbor.Tests;

[TestClass]
public sealed class ReleaseArtifactBuilderTests
{
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
            "src",
            "HomeHarbor.Tooling",
            "ReleaseArtifactBuilder.cs"));
        var compact = Regex.Replace(releaseSource, @"\s+", " ");

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
    }

    [TestMethod]
    public void OtaApply_Requires_Exact_Partition_Size_For_Root_Modules_And_Firmware()
    {
        var otaSource = File.ReadAllText(Path.Combine(
            RepositoryRoot(),
            "src",
            "HomeHarbor.Agent",
            "OtaApplyCommand.cs"));

        Assert.IsTrue(Regex.IsMatch(
            otaSource,
            @"WriteCompletePartitionImageAsync\(\s*runner,\s*plan\.RootfsPath,\s*rootDevice,",
            RegexOptions.CultureInvariant));
        Assert.IsTrue(Regex.IsMatch(
            otaSource,
            @"WriteCompletePartitionImageAsync\(\s*runner,\s*Path\.Combine\(bundleRoot, \""modules\.img\""\),\s*modulesDevice,",
            RegexOptions.CultureInvariant));
        Assert.IsTrue(Regex.IsMatch(
            otaSource,
            @"WriteCompletePartitionImageAsync\(\s*runner,\s*Path\.Combine\(bundleRoot, \""firmware\.img\""\),\s*firmwareDevice,",
            RegexOptions.CultureInvariant));
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
    public async Task DeleteManagedReleaseWorkDirectory_Uses_Constrained_Privileged_Fallback()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

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

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
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
}
