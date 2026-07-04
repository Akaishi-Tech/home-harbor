using System.Security.Cryptography;
using System.Text;
using HomeHarbor.Tooling;

namespace HomeHarbor.Tests;

[TestClass]
public sealed class ReleaseArtifactBuilderTests
{
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

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "homeharbor-release-builder-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(path);
        return path;
    }
}
