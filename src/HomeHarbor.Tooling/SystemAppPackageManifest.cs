using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HomeHarbor.Tooling;

public sealed record SystemAppPackageManifest(
    int SchemaVersion,
    string AppKey,
    string Version,
    string Channel,
    string Kind,
    string PayloadUrl,
    string PayloadSha256,
    string? KernelChannel,
    string CreatedAt);

public sealed class SystemAppPackageManifestVerifier(ICommandRunner? runner = null)
{
    private readonly ICommandRunner _runner = runner ?? new ProcessCommandRunner();

    public async Task<SystemAppPackageManifest> VerifyAsync(
        string manifestPath,
        string publicKeyPath,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException("system app manifest not found", manifestPath);
        }

        if (!File.Exists(publicKeyPath))
        {
            throw new FileNotFoundException("public key not found", publicKeyPath);
        }

        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath, cancellationToken));
        var root = doc.RootElement;
        var algorithm = StringProperty(root, "signatureAlgorithm");
        if (!string.Equals(algorithm, "Ed25519", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("unsupported or unsigned system app signature algorithm: " + (algorithm ?? "missing"));
        }

        var payload = CanonicalPayload(root);
        var payloadBytes = Encoding.UTF8.GetBytes(payload);
        var actualPayloadSha = Convert.ToHexString(SHA256.HashData(payloadBytes)).ToLowerInvariant();
        var expectedPayloadSha = StringProperty(root, "signedPayloadSha256");
        if (!string.Equals(expectedPayloadSha, actualPayloadSha, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"system app manifest payload hash mismatch: expected {expectedPayloadSha ?? "missing"}, actual {actualPayloadSha}");
        }

        var signatureText = StringProperty(root, "signature");
        if (string.IsNullOrWhiteSpace(signatureText))
        {
            throw new InvalidOperationException("system app manifest signature is missing or empty");
        }

        var work = Path.Combine(Path.GetTempPath(), "homeharbor-system-app-manifest-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(work);
        try
        {
            var payloadPath = Path.Combine(work, "manifest.payload.json");
            var signaturePath = Path.Combine(work, "manifest.signature.bin");
            await File.WriteAllTextAsync(payloadPath, payload, cancellationToken);
            await File.WriteAllBytesAsync(signaturePath, Convert.FromBase64String(signatureText), cancellationToken);
            if (new FileInfo(signaturePath).Length == 0)
            {
                throw new InvalidOperationException("system app manifest signature is missing or empty");
            }

            var result = await _runner.RunAsync(
                "openssl",
                ["pkeyutl", "-verify", "-pubin", "-inkey", publicKeyPath, "-rawin", "-in", payloadPath, "-sigfile", signaturePath],
                cancellationToken: cancellationToken);
            _ = result.EnsureSuccess("system app manifest signature verification failed");
        }
        finally
        {
            try
            {
                Directory.Delete(work, recursive: true);
            }
            catch (IOException)
            {
            }
        }

        return new SystemAppPackageManifest(
            SchemaVersion: IntProperty(root, "schemaVersion"),
            AppKey: RequiredString(root, "appKey"),
            Version: RequiredString(root, "version"),
            Channel: ReleaseChannel.Require(RequiredString(root, "channel"), "system app manifest channel"),
            Kind: RequiredString(root, "kind"),
            PayloadUrl: RequiredString(root, "payloadUrl"),
            PayloadSha256: RequiredString(root, "payloadSha256"),
            KernelChannel: StringProperty(root, "kernelChannel") is { } kernelChannel
                ? KernelChannel.Require(kernelChannel, "system app manifest kernelChannel")
                : null,
            CreatedAt: RequiredString(root, "createdAt"));
    }

    public static string CanonicalPayload(JsonElement manifest)
    {
        if (IntProperty(manifest, "schemaVersion") != 1)
        {
            throw new InvalidOperationException("system app manifest requires schemaVersion=1");
        }

        var kind = RequiredString(manifest, "kind");
        if (!string.Equals(kind, "system-app", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("system app manifest kind must be system-app");
        }

        _ = ReleaseChannel.Require(RequiredString(manifest, "channel"), "system app manifest channel");
        if (StringProperty(manifest, "kernelChannel") is { } kernelChannel)
        {
            _ = KernelChannel.Require(kernelChannel, "system app manifest kernelChannel");
            return CanonicalObject(manifest,
            [
                "appKey",
                "channel",
                "createdAt",
                "kernelChannel",
                "kind",
                "payloadSha256",
                "payloadUrl",
                "schemaVersion",
                "version"
            ]);
        }

        return CanonicalObject(manifest,
        [
            "appKey",
            "channel",
            "createdAt",
            "kind",
            "payloadSha256",
            "payloadUrl",
            "schemaVersion",
            "version"
        ]);
    }

    private static string CanonicalObject(JsonElement manifest, IReadOnlyList<string> fields)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            foreach (var field in fields)
            {
                if (!manifest.TryGetProperty(field, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                {
                    throw new InvalidOperationException("system app manifest is missing required field: " + field);
                }

                writer.WritePropertyName(field);
                value.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray()) + "\n";
    }

    private static string RequiredString(JsonElement element, string name)
        => StringProperty(element, name)
           ?? throw new InvalidOperationException("system app manifest is missing required field: " + name);

    private static string? StringProperty(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString()
            : null;

    private static int IntProperty(JsonElement element, string name)
    {
        var value = element.TryGetProperty(name, out var property)
            ? property
            : throw new InvalidOperationException("system app manifest is missing required field: " + name);

        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out number)
            ? number
            : throw new InvalidOperationException("system app manifest field must be an integer: " + name);
    }
}
