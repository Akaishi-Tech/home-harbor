using System.Text.Json;
using HomeHarbor.Tooling;

namespace HomeHarbor.Tests;

[TestClass]
public sealed class SystemAppPackageManifestTests
{
    [TestMethod]
    public void CanonicalPayload_Rejects_Unsafe_Identity_Digest_And_Url()
    {
        using (var document = Manifest("../escape", "https://example.com/payload.tar.gz", new string('a', 64)))
        {
            _ = Assert.ThrowsExactly<InvalidOperationException>(() =>
                SystemAppPackageManifestVerifier.CanonicalPayload(document.RootElement));
        }

        using (var document = Manifest("safe-app", "http://example.com/payload.tar.gz", new string('a', 64)))
        {
            var exception = Assert.ThrowsExactly<InvalidOperationException>(() =>
                SystemAppPackageManifestVerifier.CanonicalPayload(document.RootElement));
            Assert.Contains("HTTPS", exception.Message);
        }

        using (var document = Manifest("safe-app", "https://example.com/payload.tar.gz", "not-a-digest"))
        {
            var exception = Assert.ThrowsExactly<InvalidOperationException>(() =>
                SystemAppPackageManifestVerifier.CanonicalPayload(document.RootElement));
            Assert.Contains("SHA-256", exception.Message);
        }
    }

    [TestMethod]
    public void CanonicalPayload_Rejects_Private_Network_Payload_Target()
    {
        using var document = Manifest("safe-app", "https://169.254.169.254/latest/meta-data", new string('a', 64));

        var exception = Assert.ThrowsExactly<InvalidOperationException>(() =>
            SystemAppPackageManifestVerifier.CanonicalPayload(document.RootElement));

        Assert.Contains("host is not allowed", exception.Message);
    }

    private static JsonDocument Manifest(string appKey, string payloadUrl, string payloadSha256)
        => JsonDocument.Parse($$"""
            {
              "schemaVersion": 1,
              "appKey": "{{appKey}}",
              "version": "1.0.0",
              "channel": "dev",
              "kind": "system-app",
              "payloadUrl": "{{payloadUrl}}",
              "payloadSha256": "{{payloadSha256}}",
              "createdAt": "2026-07-11T00:00:00Z"
            }
            """);
}
