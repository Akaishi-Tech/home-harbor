using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HomeHarbor.Tooling;

public sealed class OtaManifestVerifier(ICommandRunner? runner = null)
{
    private readonly ICommandRunner _runner = runner ?? new ProcessCommandRunner();

    public async Task VerifyAsync(string manifestPath, string publicKeyPath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException("manifest not found", manifestPath);
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
            throw new InvalidOperationException("unsupported or unsigned OTA signature algorithm: " + (algorithm ?? "missing"));
        }

        var payload = CanonicalPayload(root);
        var payloadBytes = Encoding.UTF8.GetBytes(payload);
        var actualPayloadSha = Convert.ToHexString(SHA256.HashData(payloadBytes)).ToLowerInvariant();
        var expectedPayloadSha = StringProperty(root, "signedPayloadSha256");
        if (!string.Equals(expectedPayloadSha, actualPayloadSha, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"manifest payload hash mismatch: expected {expectedPayloadSha ?? "missing"}, actual {actualPayloadSha}");
        }

        var signatureText = StringProperty(root, "signature");
        if (string.IsNullOrWhiteSpace(signatureText))
        {
            throw new InvalidOperationException("manifest signature is missing or empty");
        }

        var work = Path.Combine(Path.GetTempPath(), "homeharbor-manifest-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(work);
        try
        {
            var payloadPath = Path.Combine(work, "manifest.payload.json");
            var signaturePath = Path.Combine(work, "manifest.signature.bin");
            await File.WriteAllTextAsync(payloadPath, payload, cancellationToken);
            await File.WriteAllBytesAsync(signaturePath, Convert.FromBase64String(signatureText), cancellationToken);
            if (new FileInfo(signaturePath).Length == 0)
            {
                throw new InvalidOperationException("manifest signature is missing or empty");
            }

            var result = await _runner.RunAsync(
                "openssl",
                ["pkeyutl", "-verify", "-pubin", "-inkey", publicKeyPath, "-rawin", "-in", payloadPath, "-sigfile", signaturePath],
                cancellationToken: cancellationToken);
            _ = result.EnsureSuccess("manifest signature verification failed");
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
    }

    public static string CanonicalPayload(JsonElement manifest)
    {
        var schemaVersion = StringProperty(manifest, "schemaVersion");
        var otaType = StringProperty(manifest, "type");
        var packageKind = StringProperty(manifest, "packageKind");
        if (schemaVersion != "1")
        {
            throw new InvalidOperationException("OTA manifest requires schemaVersion=1");
        }

        var bootMode = StringProperty(manifest, "bootMode");
        if (bootMode is not ("raw-uki" or "secure-boot-raw-uki"))
        {
            throw new InvalidOperationException("schema v1 manifest requires bootMode=raw-uki or secure-boot-raw-uki");
        }

        _ = ReleaseChannel.Require(StringProperty(manifest, "channel"), "OTA manifest channel");

        if (packageKind == "kernel" && otaType == "kernel-only")
        {
            if (HasAnyProperty(manifest, "vbmetaAHash", "vbmetaBHash", "vbmetaADigest", "vbmetaBDigest", "rootfsHash"))
            {
                if (manifest.TryGetProperty("kernelChannel", out var legacyKernelChannel) &&
                    legacyKernelChannel.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
                {
                    _ = KernelChannel.Require(StringProperty(manifest, "kernelChannel"), "OTA manifest kernelChannel");
                    var legacyKernelFields = new List<string>
                    {
                        "bootHash",
                        "bootMode"
                    };
                    AddOptionalField(legacyKernelFields, manifest, "bootloaderHash");
                    legacyKernelFields.AddRange(
                    [
                        "channel",
                        "createdAt"
                    ]);
                    AddOptionalField(legacyKernelFields, manifest, "fallbackBootHash");
                    legacyKernelFields.AddRange(
                    [
                        "firmwareHash",
                        "kernelChannel",
                        "kernelRelease",
                        "modulesHash",
                    ]);
                    AddOptionalField(legacyKernelFields, manifest, "mokManagerHash");
                    legacyKernelFields.AddRange(
                    [
                        "packageKind",
                        "rootfsHash",
                        "schemaVersion",
                        "targetSlot",
                        "type",
                        "vbmetaADigest",
                        "vbmetaAHash",
                        "vbmetaBDigest",
                        "vbmetaBHash",
                        "version"
                    ]);
                    return CanonicalObject(manifest, legacyKernelFields);
                }

                var oldKernelFields = new List<string>
                {
                    "bootHash",
                    "bootMode"
                };
                AddOptionalField(oldKernelFields, manifest, "bootloaderHash");
                oldKernelFields.AddRange(
                [
                    "channel",
                    "createdAt"
                ]);
                AddOptionalField(oldKernelFields, manifest, "fallbackBootHash");
                oldKernelFields.AddRange(
                [
                    "firmwareHash",
                    "kernelRelease",
                    "modulesHash",
                ]);
                AddOptionalField(oldKernelFields, manifest, "mokManagerHash");
                oldKernelFields.AddRange(
                [
                    "packageKind",
                    "rootfsHash",
                    "schemaVersion",
                    "targetSlot",
                    "type",
                    "vbmetaADigest",
                    "vbmetaAHash",
                    "vbmetaBDigest",
                    "vbmetaBHash",
                    "version"
                ]);
                return CanonicalObject(manifest, oldKernelFields);
            }

            _ = KernelChannel.Require(StringProperty(manifest, "kernelChannel"), "OTA manifest kernelChannel");
            var fields = new List<string>();
            if (HasAnyProperty(manifest, "addons"))
            {
                _ = ValidateAddons(manifest.GetProperty("addons"));
                fields.Add("addons");
            }

            fields.AddRange(
            [
                "bootHash",
                "bootMode"
            ]);
            AddOptionalField(fields, manifest, "bootloaderHash");
            fields.AddRange(
            [
                "channel",
                "createdAt"
            ]);
            AddOptionalField(fields, manifest, "fallbackBootHash");
            fields.AddRange(
            [
                "firmwareHash",
                "kernelChannel",
                "kernelRelease",
                "modulesHash",
            ]);
            AddOptionalField(fields, manifest, "mokManagerHash");
            fields.AddRange(
            [
                "packageKind",
                "recoveryHash",
                "schemaVersion",
                "targetSlot",
                "type",
                "version"
            ]);
            return CanonicalObject(manifest, fields);
        }

        if (packageKind == "system" && otaType == "full-system")
        {
            if (!HasAnyProperty(manifest, "bootHash", "bootloaderHash", "fallbackBootHash", "firmwareHash", "kernelRelease", "modulesHash", "recoveryHash"))
            {
                return CanonicalObject(manifest,
                [
                    "bootMode",
                    "channel",
                    "createdAt",
                    "packageKind",
                    "rootfsHash",
                    "schemaVersion",
                    "targetSlot",
                    "type",
                    "vbmetaADigest",
                    "vbmetaAHash",
                    "vbmetaBDigest",
                    "vbmetaBHash",
                    "version"
                ]);
            }

            var fields = new List<string>
            {
                "bootHash",
                "bootMode",
                "bootloaderHash",
                "channel",
                "createdAt",
                "fallbackBootHash",
                "firmwareHash",
                "kernelRelease",
                "modulesHash",
                "packageKind",
                "recoveryHash",
                "rootfsHash",
                "schemaVersion",
                "targetSlot",
                "type",
                "vbmetaADigest",
                "vbmetaAHash",
                "vbmetaBDigest",
                "vbmetaBHash",
                "version"
            };
            if (bootMode == "secure-boot-raw-uki")
            {
                fields.Insert(fields.IndexOf("packageKind"), "mokManagerHash");
            }

            return CanonicalObject(manifest, fields);
        }

        throw new InvalidOperationException("schema v1 raw UKI manifest requires packageKind=system/type=full-system or packageKind=kernel/type=kernel-only");
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
                    throw new InvalidOperationException("manifest is missing required field: " + field);
                }

                writer.WritePropertyName(field);
                if (field == "addons")
                {
                    WriteCanonicalAddons(writer, value);
                }
                else
                {
                    value.WriteTo(writer);
                }
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray()) + "\n";
    }

    private static void WriteCanonicalAddons(Utf8JsonWriter writer, JsonElement addons)
    {
        var parsed = ValidateAddons(addons);
        writer.WriteStartArray();
        foreach (var addon in parsed)
        {
            writer.WriteStartObject();
            writer.WriteString("file", addon.File);
            writer.WriteString("filesystem", addon.FileSystem);
            writer.WriteString("key", addon.Key);
            writer.WriteString("overlay", addon.Overlay);
            writer.WriteString("sha256", addon.Sha256);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static IReadOnlyList<OtaAddon> ValidateAddons(JsonElement addons)
    {
        if (addons.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("OTA manifest addons must be an array");
        }

        var result = new List<OtaAddon>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var envSuffixes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var addon in addons.EnumerateArray())
        {
            if (addon.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException("OTA manifest addon entries must be objects");
            }

            var key = RequiredString(addon, "key");
            if (string.IsNullOrEmpty(key) || !key.All(c => c is >= 'a' and <= 'z' || c is >= '0' and <= '9' || c is '.' or '_' or '-'))
            {
                throw new InvalidOperationException("OTA manifest addon key is invalid: " + key);
            }

            if (!keys.Add(key))
            {
                throw new InvalidOperationException("OTA manifest addon key is duplicated: " + key);
            }

            var envSuffix = key.Replace('.', '_').Replace('-', '_');
            if (!envSuffixes.Add(envSuffix))
            {
                throw new InvalidOperationException("OTA manifest addon key has a boot env suffix collision: " + key);
            }

            var file = RequiredString(addon, "file");
            TarSafety.ValidateMemberPath(file, "OTA manifest addon file");
            if (!file.StartsWith("addons/", StringComparison.Ordinal) || !file.EndsWith(".erofs", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("OTA manifest addon file must be under addons/ and end with .erofs: " + file);
            }

            var fileSystem = RequiredString(addon, "filesystem");
            if (!string.Equals(fileSystem, "erofs", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("OTA manifest addon filesystem must be erofs");
            }

            var overlay = RequiredString(addon, "overlay");
            if (!string.Equals(overlay, "usr", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("OTA manifest addon overlay must be usr");
            }

            var sha256 = RequiredString(addon, "sha256");
            if (sha256.Length != 64 || !sha256.All(Uri.IsHexDigit))
            {
                throw new InvalidOperationException("OTA manifest addon sha256 is invalid for " + key);
            }

            result.Add(new OtaAddon(key, file, fileSystem, overlay, sha256.ToLowerInvariant()));
        }

        return result;
    }

    private static string RequiredString(JsonElement element, string name)
        => StringProperty(element, name)
           ?? throw new InvalidOperationException("manifest is missing required field: " + name);

    private static void AddOptionalField(List<string> fields, JsonElement element, string name)
    {
        if (HasAnyProperty(element, name))
        {
            fields.Add(name);
        }
    }

    private static bool HasAnyProperty(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var property) &&
                property.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
            {
                return true;
            }
        }

        return false;
    }

    private static string? StringProperty(JsonElement element, string name)
    {
        return !element.TryGetProperty(name, out var property) || property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
            ? null
            : property.ValueKind == JsonValueKind.String ? property.GetString() : property.ToString();
    }

    private sealed record OtaAddon(string Key, string File, string FileSystem, string Overlay, string Sha256);
}
