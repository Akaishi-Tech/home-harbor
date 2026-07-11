using System.Text.Json;
using HomeHarbor.Tooling;

namespace HomeHarbor.Tests;

[TestClass]
public sealed class OtaManifestVerifierTests
{
    [TestMethod]
    public void CanonicalPayload_Builds_FullSystemPayload_Without_Slot_Field()
    {
        using var doc = JsonDocument.Parse("""
            {
              "schemaVersion": "1",
              "type": "full-system",
              "packageKind": "system",
              "releaseSequence": 42,
              "version": "0.2.0",
              "channel": "dev",
              "createdAt": "2026-07-05T00:00:00Z",
              "bootMode": "raw-uki",
              "rootfsHash": "root",
              "vbmetaAHash": "vbmeta-a",
              "vbmetaBHash": "vbmeta-b",
              "vbmetaADigest": "digest-a",
              "vbmetaBDigest": "digest-b"
            }
            """);

        var payload = OtaManifestVerifier.CanonicalPayload(doc.RootElement);

        Assert.AreEqual(
            "{\"bootMode\":\"raw-uki\",\"channel\":\"dev\",\"createdAt\":\"2026-07-05T00:00:00Z\",\"packageKind\":\"system\",\"releaseSequence\":42,\"rootfsHash\":\"root\",\"schemaVersion\":\"1\",\"type\":\"full-system\",\"vbmetaADigest\":\"digest-a\",\"vbmetaAHash\":\"vbmeta-a\",\"vbmetaBDigest\":\"digest-b\",\"vbmetaBHash\":\"vbmeta-b\",\"version\":\"0.2.0\"}\n",
            payload);
    }

    [TestMethod]
    public void CanonicalPayload_Builds_KernelPayload_Without_Slot_Field()
    {
        using var doc = JsonDocument.Parse("""
            {
              "schemaVersion": "1",
              "type": "kernel-only",
              "packageKind": "kernel",
              "releaseSequence": 42,
              "version": "0.2.0",
              "channel": "dev",
              "kernelChannel": "generic",
              "createdAt": "2026-07-05T00:00:00Z",
              "bootMode": "raw-uki",
              "kernelRelease": "6.12.0-homeharbor",
              "modulesHash": "modules",
              "firmwareHash": "firmware",
              "recoveryHash": "recovery",
              "bootHash": "boot",
              "bootloaderHash": "selector"
            }
            """);

        var payload = OtaManifestVerifier.CanonicalPayload(doc.RootElement);

        Assert.Contains("\"type\":\"kernel-only\"", payload);
        Assert.Contains("\"bootloaderHash\":\"selector\"", payload);
    }

    [TestMethod]
    public void CanonicalPayload_Rejects_Missing_ReleaseSequence()
    {
        using var doc = JsonDocument.Parse("""
            {
              "schemaVersion": "1",
              "type": "full-system",
              "packageKind": "system",
              "version": "0.2.0",
              "channel": "dev",
              "createdAt": "2026-07-05T00:00:00Z",
              "bootMode": "raw-uki"
            }
            """);

        var error = Assert.ThrowsExactly<InvalidOperationException>(
            () => OtaManifestVerifier.CanonicalPayload(doc.RootElement));

        Assert.Contains("releaseSequence", error.Message);
    }

    [TestMethod]
    [DataRow("0")]
    [DataRow("-1")]
    [DataRow("\"42\"")]
    public void ReleaseSequenceProperty_Rejects_NonPositive_Or_NonNumeric_Values(string jsonValue)
    {
        using var doc = JsonDocument.Parse("{\"releaseSequence\":" + jsonValue + "}");

        _ = Assert.ThrowsExactly<InvalidOperationException>(
            () => OtaManifestVerifier.ReleaseSequenceProperty(doc.RootElement));
    }
}
