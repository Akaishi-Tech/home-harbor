using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

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

public sealed partial class SystemAppPackageManifestVerifier(ICommandRunner? runner = null)
{
    public const int MaxManifestBytes = 1024 * 1024;
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

        var manifestLength = new FileInfo(manifestPath).Length;
        if (manifestLength is < 1 or > MaxManifestBytes)
        {
            throw new InvalidOperationException($"system app manifest must be between 1 and {MaxManifestBytes} bytes");
        }

        using var doc = JsonDocument.Parse(
            await File.ReadAllTextAsync(manifestPath, cancellationToken),
            new JsonDocumentOptions { MaxDepth = 16 });
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
            var signature = Convert.FromBase64String(signatureText);
            if (signature.Length != 64)
            {
                throw new InvalidOperationException("system app manifest Ed25519 signature must be exactly 64 bytes");
            }

            await File.WriteAllBytesAsync(signaturePath, signature, cancellationToken);

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
            AppKey: ValidateAppKey(RequiredString(root, "appKey")),
            Version: ValidateVersion(RequiredString(root, "version")),
            Channel: ReleaseChannel.Require(RequiredString(root, "channel"), "system app manifest channel"),
            Kind: RequiredString(root, "kind"),
            PayloadUrl: ValidatePayloadUrl(RequiredString(root, "payloadUrl")),
            PayloadSha256: ValidateSha256(RequiredString(root, "payloadSha256")),
            KernelChannel: StringProperty(root, "kernelChannel") is { } kernelChannel
                ? KernelChannel.Require(kernelChannel, "system app manifest kernelChannel")
                : null,
            CreatedAt: ValidateTimestamp(RequiredString(root, "createdAt")));
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
        _ = ValidateAppKey(RequiredString(manifest, "appKey"));
        _ = ValidateVersion(RequiredString(manifest, "version"));
        _ = ValidatePayloadUrl(RequiredString(manifest, "payloadUrl"));
        _ = ValidateSha256(RequiredString(manifest, "payloadSha256"));
        _ = ValidateTimestamp(RequiredString(manifest, "createdAt"));
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
            ? value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : throw new InvalidOperationException("system app manifest field must be a string: " + name)
            : null;

    private static int IntProperty(JsonElement element, string name)
    {
        var value = element.TryGetProperty(name, out var property)
            ? property
            : throw new InvalidOperationException("system app manifest is missing required field: " + name);

        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : throw new InvalidOperationException("system app manifest field must be an integer: " + name);
    }

    private static string ValidateAppKey(string value)
    {
        var appKey = value.Trim();
        return AppKeyRegex().IsMatch(appKey)
            ? appKey
            : throw new InvalidOperationException("system app manifest appKey is invalid: " + value);
    }

    private static string ValidateVersion(string value)
    {
        var version = value.Trim();
        return VersionRegex().IsMatch(version)
            ? version
            : throw new InvalidOperationException("system app manifest version is invalid: " + value);
    }

    private static string ValidatePayloadUrl(string value)
    {
        var text = value.Trim();
        if (text.Length is < 1 or > 2048 || !Uri.TryCreate(text, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeFile) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment) ||
            (uri.Scheme == Uri.UriSchemeFile && !string.IsNullOrEmpty(uri.Host)))
        {
            throw new InvalidOperationException("system app manifest payloadUrl must be an HTTPS URL or a local file URL without credentials or fragments");
        }

        if (uri.Scheme == Uri.UriSchemeHttps)
        {
            _ = BoundedUriFetcher.ValidateUri(text, label: "system app payload URL");
        }

        return text;
    }

    private static string ValidateSha256(string value)
    {
        var digest = value.Trim().ToLowerInvariant();
        return Sha256Regex().IsMatch(digest)
            ? digest
            : throw new InvalidOperationException("system app manifest payloadSha256 must be a SHA-256 hex digest");
    }

    private static string ValidateTimestamp(string value)
        => value.Length <= 64 &&
           DateTimeOffset.TryParse(
               value,
               System.Globalization.CultureInfo.InvariantCulture,
               System.Globalization.DateTimeStyles.RoundtripKind,
               out _)
            ? value
            : throw new InvalidOperationException("system app manifest createdAt must be an ISO-8601 timestamp");

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex AppKeyRegex();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._+-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionRegex();

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Regex();
}
