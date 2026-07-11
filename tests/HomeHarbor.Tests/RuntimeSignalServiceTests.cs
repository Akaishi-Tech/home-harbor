using System.Text.Json;
using HomeHarbor.Api.Services;
using Microsoft.Extensions.Options;

namespace HomeHarbor.Tests;

[TestClass]
public sealed class RuntimeSignalServiceTests
{
    [TestMethod]
    public async Task Smb_Credential_Writes_Are_Atomic_Owner_Only_And_Valid_Json()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var credentials = Path.Combine(root, "credentials");
            var service = CreateService(root, credentials);
            var credentialId = Guid.NewGuid();

            await Task.WhenAll(Enumerable.Range(0, 16).Select(index =>
                service.WriteSmbPasswordAsync(
                    credentialId,
                    "family-user",
                    "homeharbor-smb001",
                    $"password-{index}",
                    CancellationToken.None)));

            var path = Path.Combine(credentials, $"{credentialId:N}.json");
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            Assert.AreEqual("upsert", document.RootElement.GetProperty("action").GetString());
            Assert.AreEqual(credentialId, document.RootElement.GetProperty("credentialId").GetGuid());
            Assert.AreEqual("family-user", document.RootElement.GetProperty("username").GetString());
            Assert.AreEqual("homeharbor-smb001", document.RootElement.GetProperty("unixUser").GetString());
            Assert.IsTrue(document.RootElement.GetProperty("password").GetString()!.StartsWith("password-", StringComparison.Ordinal));
            AssertOwnerOnly(path);
            Assert.AreEqual(0, Directory.EnumerateFiles(credentials, "*.tmp", SearchOption.TopDirectoryOnly).Count());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task Smb_Revoke_Replaces_Existing_Payload_Atomically()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var credentials = Path.Combine(root, "credentials");
            var service = CreateService(root, credentials);
            var credentialId = Guid.NewGuid();
            await service.WriteSmbPasswordAsync(
                credentialId,
                "family-user",
                "homeharbor-smb001",
                "not-returned",
                CancellationToken.None);

            await service.WriteSmbRevokeAsync(
                credentialId,
                "family-user",
                "homeharbor-smb001",
                CancellationToken.None);

            var path = Path.Combine(credentials, $"{credentialId:N}.json");
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            Assert.AreEqual("revoke", document.RootElement.GetProperty("action").GetString());
            Assert.IsFalse(document.RootElement.TryGetProperty("password", out _));
            AssertOwnerOnly(path);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void Runtime_Requests_Are_Atomically_Replaced_With_Owner_Only_Mode()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var requests = Path.Combine(root, "requests");
            var service = CreateService(requests, Path.Combine(root, "credentials"));

            service.RequestSmbApply();
            service.RequestSmbApply();
            service.RequestContainerApply();
            service.RequestSystemAppApply();
            service.RequestCaddyRender();

            foreach (var file in Directory.EnumerateFiles(requests, "*.request"))
            {
                Assert.IsTrue(long.TryParse(File.ReadAllText(file), out _));
                AssertOwnerOnly(file);
            }

            Assert.AreEqual(4, Directory.EnumerateFiles(requests, "*.request").Count());
            Assert.AreEqual(0, Directory.EnumerateFiles(requests, "*.tmp").Count());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static RuntimeSignalService CreateService(string requestDirectory, string credentialDirectory)
        => new(Options.Create(new HomeHarborRuntimeOptions
        {
            RequestDirectory = requestDirectory,
            SmbCredentialDirectory = credentialDirectory
        }));

    private static void AssertOwnerOnly(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.AreEqual(
                UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(path));
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"homeharbor-runtime-signals-{Guid.NewGuid():N}");
        _ = Directory.CreateDirectory(path);
        return path;
    }
}
