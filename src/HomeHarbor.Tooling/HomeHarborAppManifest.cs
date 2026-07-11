using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HomeHarbor.Tooling;

public sealed record HomeHarborAppManifest(
    int SchemaVersion,
    string Kind,
    string AppKey,
    string Version,
    string Channel,
    string DisplayName,
    string Title,
    string Description,
    string Category,
    bool RecommendedInSetup,
    IReadOnlyList<string> VisibleRoles,
    HomeHarborAppInstall Install,
    string Source = "built-in");

public abstract record HomeHarborAppInstall(string Type);

public sealed record HomeHarborContainerAppInstall(
    string Image,
    IReadOnlyList<HomeHarborAppPort> Ports,
    IReadOnlyDictionary<string, string> Environment,
    IReadOnlyList<HomeHarborAppVolume> Volumes,
    IReadOnlyList<string> Command) : HomeHarborAppInstall("container");

public sealed record HomeHarborSystemAppInstall(
    string Mode,
    string ManifestUrl,
    IReadOnlyList<string> Commands,
    HomeHarborAppHotCheck? HotCheck,
    IReadOnlyList<string> AutoInstallWhenKernelModulesPresent) : HomeHarborAppInstall("system");

public sealed record HomeHarborAppPort(int HostPort, int ContainerPort, string Protocol);

public sealed record HomeHarborAppVolume(string? HostPath, string ContainerPath, bool ReadOnly);

public sealed record HomeHarborAppHotCheck(string Command, IReadOnlyList<string> Args);

public sealed record HomeHarborAppStoreIndex(
    int SchemaVersion,
    string Kind,
    string Channel,
    string GeneratedAt,
    IReadOnlyList<HomeHarborAppStoreEntry> Apps);

public sealed record HomeHarborAppStoreEntry(
    string AppKey,
    string Version,
    string ManifestUrl,
    string ManifestSha256);

public sealed partial class HomeHarborAppManifestVerifier(ICommandRunner? runner = null)
{
    public const int MaxManifestBytes = 1024 * 1024;
    public const int MaxStoreEntries = 256;
    public const int CurrentSchemaVersion = 1;
    public const string AppKind = "homeharbor.app";
    public const string StoreKind = "homeharbor.app-store";
    public const string SignatureAlgorithm = "Ed25519";

    private readonly ICommandRunner _runner = runner ?? new ProcessCommandRunner();

    public async Task<HomeHarborAppManifest> VerifyAppManifestJsonAsync(
        string json,
        string publicKeyPath,
        string source = "remote",
        CancellationToken cancellationToken = default)
    {
        RefuseOversizedJson(json, "app manifest");
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
        var payload = CanonicalAppPayload(doc.RootElement);
        await VerifySignatureAsync(doc.RootElement, payload, publicKeyPath, "app manifest", cancellationToken);
        return ParseTrustedAppManifest(doc.RootElement, source);
    }

    public async Task<HomeHarborAppStoreIndex> VerifyStoreIndexJsonAsync(
        string json,
        string publicKeyPath,
        CancellationToken cancellationToken = default)
    {
        RefuseOversizedJson(json, "app store index");
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
        var payload = CanonicalStoreIndexPayload(doc.RootElement);
        await VerifySignatureAsync(doc.RootElement, payload, publicKeyPath, "app store index", cancellationToken);
        return ParseTrustedStoreIndex(doc.RootElement);
    }

    public static HomeHarborAppManifest ParseTrustedAppManifest(JsonElement manifest, string source = "built-in")
    {
        _ = CanonicalAppPayload(manifest);
        var install = RequiredProperty(manifest, "install");
        var installType = RequiredString(install, "type");
        HomeHarborAppInstall parsedInstall = installType switch
        {
            "container" => ParseContainerInstall(install),
            "system" => ParseSystemInstall(install),
            _ => throw new InvalidOperationException("HHAF install.type must be container or system")
        };

        return new HomeHarborAppManifest(
            SchemaVersion: IntProperty(manifest, "schemaVersion"),
            Kind: RequiredString(manifest, "kind"),
            AppKey: ValidateAppKey(RequiredString(manifest, "appKey")),
            Version: ValidateVersion(RequiredString(manifest, "version")),
            Channel: ReleaseChannel.Require(RequiredString(manifest, "channel"), "HHAF app channel"),
            DisplayName: ValidateDisplayText(RequiredString(manifest, "displayName"), "displayName", 96),
            Title: ValidateDisplayText(RequiredString(manifest, "title"), "title", 96),
            Description: ValidateDisplayText(RequiredString(manifest, "description"), "description", 512),
            Category: ValidateCategory(RequiredString(manifest, "category")),
            RecommendedInSetup: BoolProperty(manifest, "recommendedInSetup"),
            VisibleRoles: ReadStringArray(manifest, "visibleRoles", 16, 32).Select(ValidateRole).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            Install: parsedInstall,
            Source: source);
    }

    public static HomeHarborAppStoreIndex ParseTrustedStoreIndex(JsonElement index)
    {
        _ = CanonicalStoreIndexPayload(index);
        return new HomeHarborAppStoreIndex(
            SchemaVersion: IntProperty(index, "schemaVersion"),
            Kind: RequiredString(index, "kind"),
            Channel: ReleaseChannel.Require(RequiredString(index, "channel"), "HHAF store index channel"),
            GeneratedAt: ValidateTimestamp(RequiredString(index, "generatedAt"), "generatedAt"),
            Apps: RequiredProperty(index, "apps").EnumerateArray().Select(ParseStoreEntry).ToArray());
    }

    public static string CanonicalAppPayload(JsonElement manifest)
    {
        if (IntProperty(manifest, "schemaVersion") != CurrentSchemaVersion)
        {
            throw new InvalidOperationException("HHAF app manifest requires schemaVersion=1");
        }

        var kind = RequiredString(manifest, "kind");
        if (!string.Equals(kind, AppKind, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("HHAF app manifest kind must be homeharbor.app");
        }

        _ = ValidateAppKey(RequiredString(manifest, "appKey"));
        _ = ValidateVersion(RequiredString(manifest, "version"));
        _ = ReleaseChannel.Require(RequiredString(manifest, "channel"), "HHAF app channel");
        _ = ValidateDisplayText(RequiredString(manifest, "displayName"), "displayName", 96);
        _ = ValidateDisplayText(RequiredString(manifest, "title"), "title", 96);
        _ = ValidateDisplayText(RequiredString(manifest, "description"), "description", 512);
        _ = ValidateCategory(RequiredString(manifest, "category"));
        _ = BoolProperty(manifest, "recommendedInSetup");
        foreach (var role in ReadStringArray(manifest, "visibleRoles", 16, 32))
        {
            _ = ValidateRole(role);
        }

        var install = RequiredProperty(manifest, "install");
        ValidateInstall(install);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            WriteRequiredProperty(writer, manifest, "schemaVersion");
            WriteRequiredProperty(writer, manifest, "kind");
            WriteRequiredProperty(writer, manifest, "appKey");
            WriteRequiredProperty(writer, manifest, "version");
            WriteRequiredProperty(writer, manifest, "channel");
            WriteRequiredProperty(writer, manifest, "displayName");
            WriteRequiredProperty(writer, manifest, "title");
            WriteRequiredProperty(writer, manifest, "description");
            WriteRequiredProperty(writer, manifest, "category");
            WriteRequiredProperty(writer, manifest, "recommendedInSetup");
            WriteStringArray(writer, "visibleRoles", ReadStringArray(manifest, "visibleRoles", 16, 32));
            writer.WritePropertyName("install");
            WriteCanonicalInstall(writer, install);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray()) + "\n";
    }

    public static string CanonicalStoreIndexPayload(JsonElement index)
    {
        if (IntProperty(index, "schemaVersion") != CurrentSchemaVersion)
        {
            throw new InvalidOperationException("HHAF store index requires schemaVersion=1");
        }

        var kind = RequiredString(index, "kind");
        if (!string.Equals(kind, StoreKind, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("HHAF store index kind must be homeharbor.app-store");
        }

        _ = ReleaseChannel.Require(RequiredString(index, "channel"), "HHAF store index channel");
        _ = ValidateTimestamp(RequiredString(index, "generatedAt"), "generatedAt");
        var apps = RequiredProperty(index, "apps");
        if (apps.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("HHAF store index apps must be an array");
        }

        var parsedApps = apps.EnumerateArray().Select(ParseStoreEntry).ToArray();
        if (parsedApps.Length > MaxStoreEntries)
        {
            throw new InvalidOperationException($"HHAF store index may contain at most {MaxStoreEntries} apps");
        }

        if (parsedApps.Select(app => app.AppKey).Distinct(StringComparer.Ordinal).Count() != parsedApps.Length)
        {
            throw new InvalidOperationException("HHAF store index contains duplicate app keys");
        }
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            WriteRequiredProperty(writer, index, "schemaVersion");
            WriteRequiredProperty(writer, index, "kind");
            WriteRequiredProperty(writer, index, "channel");
            WriteRequiredProperty(writer, index, "generatedAt");
            writer.WritePropertyName("apps");
            writer.WriteStartArray();
            foreach (var app in parsedApps)
            {
                writer.WriteStartObject();
                writer.WriteString("appKey", app.AppKey);
                writer.WriteString("version", app.Version);
                writer.WriteString("manifestUrl", app.ManifestUrl);
                writer.WriteString("manifestSha256", app.ManifestSha256);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray()) + "\n";
    }

    public static string Sha256Hex(string text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private async Task VerifySignatureAsync(
        JsonElement root,
        string canonicalPayload,
        string publicKeyPath,
        string label,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(publicKeyPath))
        {
            throw new FileNotFoundException("public key not found", publicKeyPath);
        }

        var algorithm = StringProperty(root, "signatureAlgorithm");
        if (!string.Equals(algorithm, SignatureAlgorithm, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"unsupported or unsigned {label} signature algorithm: {algorithm ?? "missing"}");
        }

        var payloadBytes = Encoding.UTF8.GetBytes(canonicalPayload);
        var actualPayloadSha = Convert.ToHexString(SHA256.HashData(payloadBytes)).ToLowerInvariant();
        var expectedPayloadSha = StringProperty(root, "signedPayloadSha256");
        if (!string.Equals(expectedPayloadSha, actualPayloadSha, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"{label} payload hash mismatch: expected {expectedPayloadSha ?? "missing"}, actual {actualPayloadSha}");
        }

        var signatureText = StringProperty(root, "signature");
        if (string.IsNullOrWhiteSpace(signatureText))
        {
            throw new InvalidOperationException($"{label} signature is missing or empty");
        }

        var work = Path.Combine(Path.GetTempPath(), "homeharbor-hhaf-signature-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(work);
        try
        {
            var payloadPath = Path.Combine(work, "payload.json");
            var signaturePath = Path.Combine(work, "signature.bin");
            await File.WriteAllTextAsync(payloadPath, canonicalPayload, cancellationToken);
            var signature = Convert.FromBase64String(signatureText);
            if (signature.Length != 64)
            {
                throw new InvalidOperationException($"{label} Ed25519 signature must be exactly 64 bytes");
            }

            await File.WriteAllBytesAsync(signaturePath, signature, cancellationToken);

            var result = await _runner.RunAsync(
                "openssl",
                ["pkeyutl", "-verify", "-pubin", "-inkey", publicKeyPath, "-rawin", "-in", payloadPath, "-sigfile", signaturePath],
                cancellationToken: cancellationToken);
            _ = result.EnsureSuccess($"{label} signature verification failed");
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

    private static HomeHarborContainerAppInstall ParseContainerInstall(JsonElement install)
        => new(
            Image: ValidateContainerImage(RequiredString(install, "image")),
            Ports: ReadPorts(install),
            Environment: ReadEnvironment(install),
            Volumes: ReadVolumes(install),
            Command: ReadCommandArguments(install, "command", 64));

    private static HomeHarborSystemAppInstall ParseSystemInstall(JsonElement install)
        => new(
            Mode: ValidateSystemInstallMode(RequiredString(install, "mode")),
            ManifestUrl: ValidateAbsoluteDownloadUrl(RequiredString(install, "manifestUrl"), "manifestUrl"),
            Commands: ReadStringArray(install, "commands", 32, 64).Select(ValidateCommandName).ToArray(),
            HotCheck: ReadHotCheck(install),
            AutoInstallWhenKernelModulesPresent: ReadAutoInstallModules(install));

    private static HomeHarborAppStoreEntry ParseStoreEntry(JsonElement entry)
        => new(
            AppKey: ValidateAppKey(RequiredString(entry, "appKey")),
            Version: ValidateVersion(RequiredString(entry, "version")),
            ManifestUrl: ValidateAbsoluteDownloadUrl(RequiredString(entry, "manifestUrl"), "manifestUrl"),
            ManifestSha256: ValidateSha256(RequiredString(entry, "manifestSha256"), "manifestSha256"));

    private static void ValidateInstall(JsonElement install)
    {
        var installType = RequiredString(install, "type");
        switch (installType)
        {
            case "container":
                _ = ValidateContainerImage(RequiredString(install, "image"));
                _ = ReadPorts(install);
                _ = ReadEnvironment(install);
                _ = ReadVolumes(install);
                _ = ReadCommandArguments(install, "command", 64);
                break;
            case "system":
                _ = ValidateSystemInstallMode(RequiredString(install, "mode"));
                _ = ValidateAbsoluteDownloadUrl(RequiredString(install, "manifestUrl"), "manifestUrl");
                var commands = ReadStringArray(install, "commands", 32, 64).Select(ValidateCommandName).ToArray();
                if (commands.Length == 0)
                {
                    throw new InvalidOperationException("HHAF system install commands cannot be empty");
                }

                _ = ReadHotCheck(install);
                foreach (var module in ReadAutoInstallModules(install))
                {
                    _ = ValidateModuleName(module);
                }

                break;
            default:
                throw new InvalidOperationException("HHAF install.type must be container or system");
        }
    }

    private static void WriteCanonicalInstall(Utf8JsonWriter writer, JsonElement install)
    {
        var type = RequiredString(install, "type");
        writer.WriteStartObject();
        writer.WriteString("type", type);
        switch (type)
        {
            case "container":
                writer.WriteString("image", ValidateContainerImage(RequiredString(install, "image")));
                WritePorts(writer, ReadPorts(install));
                WriteEnvironment(writer, ReadEnvironment(install));
                WriteVolumes(writer, ReadVolumes(install));
                WriteStringArray(writer, "command", ReadCommandArguments(install, "command", 64));
                break;
            case "system":
                writer.WriteString("mode", ValidateSystemInstallMode(RequiredString(install, "mode")));
                writer.WriteString("manifestUrl", ValidateAbsoluteDownloadUrl(RequiredString(install, "manifestUrl"), "manifestUrl"));
                WriteStringArray(writer, "commands", ReadStringArray(install, "commands", 32, 64).Select(ValidateCommandName).ToArray());
                if (ReadHotCheck(install) is { } hotCheck)
                {
                    writer.WritePropertyName("hotCheck");
                    writer.WriteStartObject();
                    writer.WriteString("command", hotCheck.Command);
                    WriteStringArray(writer, "args", hotCheck.Args);
                    writer.WriteEndObject();
                }

                var modules = ReadAutoInstallModules(install);
                if (modules.Count > 0)
                {
                    writer.WritePropertyName("autoInstall");
                    writer.WriteStartObject();
                    WriteStringArray(writer, "whenKernelModulesPresent", modules);
                    writer.WriteEndObject();
                }

                break;
            default:
                throw new InvalidOperationException("HHAF install.type must be container or system");
        }

        writer.WriteEndObject();
    }

    private static IReadOnlyList<HomeHarborAppPort> ReadPorts(JsonElement install)
    {
        if (!install.TryGetProperty("ports", out var ports) || ports.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return [];
        }

        if (ports.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("HHAF container ports must be an array");
        }

        var result = new List<HomeHarborAppPort>();
        foreach (var port in ports.EnumerateArray())
        {
            if (result.Count >= 16)
            {
                throw new InvalidOperationException("HHAF container may publish at most 16 ports");
            }

            var protocol = StringProperty(port, "protocol") ?? "tcp";
            if (protocol is not ("tcp" or "udp"))
            {
                throw new InvalidOperationException("HHAF container port protocol must be tcp or udp");
            }

            var hostPort = IntProperty(port, "hostPort");
            var containerPort = IntProperty(port, "containerPort");
            if (hostPort is < 1024 or > 65535)
            {
                throw new InvalidOperationException("HHAF rootless container host ports must be between 1024 and 65535");
            }

            if (containerPort is < 1 or > 65535)
            {
                throw new InvalidOperationException("HHAF container ports must be between 1 and 65535");
            }

            if (result.Any(existing => existing.HostPort == hostPort && existing.Protocol == protocol))
            {
                throw new InvalidOperationException($"HHAF container contains duplicate host port {hostPort}/{protocol}");
            }

            result.Add(new HomeHarborAppPort(hostPort, containerPort, protocol));
        }

        return result;
    }

    private static IReadOnlyDictionary<string, string> ReadEnvironment(JsonElement install)
    {
        if (!install.TryGetProperty("environment", out var environment) || environment.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return new SortedDictionary<string, string>(StringComparer.Ordinal);
        }

        if (environment.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("HHAF container environment must be an object");
        }

        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in environment.EnumerateObject())
        {
            if (result.Count >= 64)
            {
                throw new InvalidOperationException("HHAF container may define at most 64 environment variables");
            }

            if (property.Name.Length > 64 || !EnvironmentKeyRegex().IsMatch(property.Name))
            {
                throw new InvalidOperationException($"HHAF environment variable is invalid: {property.Name}");
            }

            if (property.Value.ValueKind != JsonValueKind.String)
            {
                throw new InvalidOperationException($"HHAF environment value must be a string: {property.Name}");
            }

            var value = property.Value.GetString() ?? string.Empty;
            if (value.Length > 4096 || value.Any(char.IsControl))
            {
                throw new InvalidOperationException($"HHAF environment value is invalid: {property.Name}");
            }

            result[property.Name] = value;
        }

        return result;
    }

    private static IReadOnlyList<HomeHarborAppVolume> ReadVolumes(JsonElement install)
    {
        if (!install.TryGetProperty("volumes", out var volumes) || volumes.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return [];
        }

        if (volumes.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("HHAF container volumes must be an array");
        }

        var result = new List<HomeHarborAppVolume>();
        foreach (var volume in volumes.EnumerateArray())
        {
            if (result.Count >= 16)
            {
                throw new InvalidOperationException("HHAF container may mount at most 16 volumes");
            }

            var hostPath = StringProperty(volume, "hostPath");
            if (hostPath is not null && (hostPath.Length > 4096 || hostPath.Any(char.IsControl) || hostPath.Contains(':', StringComparison.Ordinal)))
            {
                throw new InvalidOperationException("HHAF volume hostPath is invalid");
            }

            var containerPath = ValidateContainerPath(RequiredString(volume, "containerPath"));
            result.Add(new HomeHarborAppVolume(hostPath, containerPath, BoolProperty(volume, "readOnly")));
        }

        return result;
    }

    private static HomeHarborAppHotCheck? ReadHotCheck(JsonElement install)
    {
        return !install.TryGetProperty("hotCheck", out var hotCheck) || hotCheck.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
            ? null
            : hotCheck.ValueKind != JsonValueKind.Object
            ? throw new InvalidOperationException("HHAF system hotCheck must be an object")
            : new HomeHarborAppHotCheck(
            Command: ValidateCommandName(RequiredString(hotCheck, "command")),
            Args: ReadCommandArguments(hotCheck, "args", 32));
    }

    private static IReadOnlyList<string> ReadAutoInstallModules(JsonElement install)
    {
        return !install.TryGetProperty("autoInstall", out var autoInstall) ||
            autoInstall.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
            ? []
            : autoInstall.ValueKind != JsonValueKind.Object
            ? throw new InvalidOperationException("HHAF system autoInstall must be an object")
            : [.. ReadStringArray(autoInstall, "whenKernelModulesPresent", 32, 64).Select(ValidateModuleName)];
    }

    private static void WritePorts(Utf8JsonWriter writer, IReadOnlyList<HomeHarborAppPort> ports)
    {
        writer.WritePropertyName("ports");
        writer.WriteStartArray();
        foreach (var port in ports)
        {
            writer.WriteStartObject();
            writer.WriteNumber("hostPort", port.HostPort);
            writer.WriteNumber("containerPort", port.ContainerPort);
            writer.WriteString("protocol", port.Protocol);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteEnvironment(Utf8JsonWriter writer, IReadOnlyDictionary<string, string> environment)
    {
        writer.WritePropertyName("environment");
        writer.WriteStartObject();
        foreach (var (key, value) in environment.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            writer.WriteString(key, value);
        }

        writer.WriteEndObject();
    }

    private static void WriteVolumes(Utf8JsonWriter writer, IReadOnlyList<HomeHarborAppVolume> volumes)
    {
        writer.WritePropertyName("volumes");
        writer.WriteStartArray();
        foreach (var volume in volumes)
        {
            writer.WriteStartObject();
            if (volume.HostPath is not null)
            {
                writer.WriteString("hostPath", volume.HostPath);
            }

            writer.WriteString("containerPath", volume.ContainerPath);
            writer.WriteBoolean("readOnly", volume.ReadOnly);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteRequiredProperty(Utf8JsonWriter writer, JsonElement element, string name)
    {
        writer.WritePropertyName(name);
        RequiredProperty(element, name).WriteTo(writer);
    }

    private static void WriteStringArray(Utf8JsonWriter writer, string name, IReadOnlyList<string> values)
    {
        writer.WritePropertyName(name);
        writer.WriteStartArray();
        foreach (var value in values)
        {
            writer.WriteStringValue(value);
        }

        writer.WriteEndArray();
    }

    private static IReadOnlyList<string> ReadStringArray(
        JsonElement element,
        string name,
        int maxItems,
        int maxItemLength)
    {
        if (!element.TryGetProperty(name, out var property) || property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return [];
        }

        if (property.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException($"HHAF field must be an array: {name}");
        }

        var result = new List<string>();
        foreach (var value in property.EnumerateArray())
        {
            if (result.Count >= maxItems)
            {
                throw new InvalidOperationException($"HHAF field contains too many items: {name}");
            }

            if (value.ValueKind != JsonValueKind.String)
            {
                throw new InvalidOperationException($"HHAF field must contain only strings: {name}");
            }

            var text = value.GetString() ?? string.Empty;
            if (text.Length > maxItemLength)
            {
                throw new InvalidOperationException($"HHAF field contains an oversized item: {name}");
            }

            result.Add(text);
        }

        return result;
    }

    private static IReadOnlyList<string> ReadCommandArguments(JsonElement element, string name, int maxItems)
        => ReadStringArray(element, name, maxItems, 512)
            .Select((value, index) => ValidateCommandArgument(value, $"{name}[{index}]"))
            .ToArray();

    private static JsonElement RequiredProperty(JsonElement element, string name)
    {
        return !element.TryGetProperty(name, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
            ? throw new InvalidOperationException("HHAF manifest is missing required field: " + name)
            : value;
    }

    private static string RequiredString(JsonElement element, string name)
        => StringProperty(element, name)
           ?? throw new InvalidOperationException("HHAF manifest is missing required field: " + name);

    private static string? StringProperty(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : throw new InvalidOperationException("HHAF manifest field must be a string: " + name)
            : null;

    private static bool BoolProperty(JsonElement element, string name)
    {
        var value = RequiredProperty(element, name);
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new InvalidOperationException("HHAF manifest field must be a boolean: " + name)
        };
    }

    private static int IntProperty(JsonElement element, string name)
    {
        var value = RequiredProperty(element, name);
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : throw new InvalidOperationException("HHAF manifest field must be an integer: " + name);
    }

    private static string ValidateAppKey(string value)
    {
        var appKey = value.Trim();
        return !AppKeyRegex().IsMatch(appKey) ? throw new InvalidOperationException("HHAF appKey is invalid: " + value) : appKey;
    }

    private static string ValidateVersion(string value)
    {
        var version = value.Trim();
        return !VersionRegex().IsMatch(version)
            ? throw new InvalidOperationException("HHAF version is invalid: " + value)
            : version;
    }

    private static string ValidateDisplayText(string value, string name, int maxLength)
    {
        var text = value.Trim();
        return text.Length is < 1 || text.Length > maxLength || text.Any(char.IsControl)
            ? throw new InvalidOperationException($"HHAF {name} is invalid")
            : text;
    }

    private static string ValidateCategory(string value)
    {
        var category = value.Trim();
        return !CategoryRegex().IsMatch(category) ? throw new InvalidOperationException("HHAF category is invalid: " + value) : category;
    }

    private static string ValidateContainerImage(string value)
    {
        var image = value.Trim();
        if (image.Length is < 1 or > 512 ||
            image.StartsWith('-') ||
            image.Any(char.IsWhiteSpace) ||
            image.Any(char.IsControl) ||
            !ContainerImageDigestRegex().IsMatch(image))
        {
            throw new InvalidOperationException(
                "HHAF container image must be an untagged repository reference ending in @sha256:<64 lowercase hex characters>");
        }

        var digestSeparator = image.LastIndexOf("@sha256:", StringComparison.Ordinal);
        var repository = image[..digestSeparator];
        if (repository.LastIndexOf(':') > repository.LastIndexOf('/'))
        {
            throw new InvalidOperationException(
                "HHAF container image cannot combine a tag with a digest; use repository@sha256:<digest>");
        }

        return image;
    }

    private static string ValidateRole(string value)
    {
        var role = value.Trim();
        return !RoleRegex().IsMatch(role) ? throw new InvalidOperationException("HHAF visible role is invalid: " + value) : role;
    }

    public static string ValidateCommandName(string value)
    {
        var command = value.Trim();
        return !CommandRegex().IsMatch(command) ? throw new InvalidOperationException("HHAF system command is invalid: " + value) : command;
    }

    public static string ValidateModuleName(string value)
    {
        var module = value.Trim();
        return !ModuleRegex().IsMatch(module) ? throw new InvalidOperationException("HHAF kernel module name is invalid: " + value) : module;
    }

    private static string ValidateAbsoluteDownloadUrl(string value, string name)
    {
        var text = value.Trim();
        if (text.Length is < 1 or > 2048 || !Uri.TryCreate(text, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeFile) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment) ||
            (uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrWhiteSpace(uri.IdnHost)) ||
            (uri.Scheme == Uri.UriSchemeFile && !string.IsNullOrEmpty(uri.Host)))
        {
            throw new InvalidOperationException($"HHAF {name} must be an absolute HTTPS URL or a local file URL without credentials or fragments");
        }

        if (uri.Scheme == Uri.UriSchemeHttps)
        {
            _ = BoundedUriFetcher.ValidateUri(text, label: $"HHAF {name}");
        }

        return text;
    }

    private static string ValidateSystemInstallMode(string value)
        => string.Equals(value, "usr-overlay", StringComparison.Ordinal)
            ? value
            : throw new InvalidOperationException("HHAF system install mode must be usr-overlay");

    private static string ValidateContainerPath(string value)
    {
        var path = value.Trim();
        if (path.Length is < 2 or > 4096 || !path.StartsWith('/') || path.Any(char.IsControl) ||
            path.Any(char.IsWhiteSpace) || path.Contains(':', StringComparison.Ordinal) ||
            path.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(segment => segment is "." or ".."))
        {
            throw new InvalidOperationException("HHAF volume containerPath is invalid: " + value);
        }

        return path.TrimEnd('/');
    }

    private static string ValidateCommandArgument(string value, string name)
        => value.Length == 0 || value.Any(char.IsControl)
            ? throw new InvalidOperationException($"HHAF command argument is invalid: {name}")
            : value;

    private static string ValidateTimestamp(string value, string name)
        => value.Length <= 64 &&
           DateTimeOffset.TryParse(
               value,
               System.Globalization.CultureInfo.InvariantCulture,
               System.Globalization.DateTimeStyles.RoundtripKind,
               out _)
            ? value
            : throw new InvalidOperationException($"HHAF {name} must be an ISO-8601 timestamp");

    private static void RefuseOversizedJson(string json, string label)
    {
        if (Encoding.UTF8.GetByteCount(json) > MaxManifestBytes)
        {
            throw new InvalidOperationException($"HHAF {label} exceeds the {MaxManifestBytes}-byte limit");
        }
    }

    private static string ValidateSha256(string value, string name)
    {
        var text = value.Trim().ToLowerInvariant();
        return !Sha256Regex().IsMatch(text) ? throw new InvalidOperationException($"HHAF {name} must be a SHA-256 hex digest") : text;
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex AppKeyRegex();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._+-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionRegex();

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex CategoryRegex();

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9._-]{0,31}$", RegexOptions.CultureInvariant)]
    private static partial Regex RoleRegex();

    [GeneratedRegex("^[A-Za-z0-9._+-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex CommandRegex();

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex ModuleRegex();

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex EnvironmentKeyRegex();

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Regex();

    [GeneratedRegex("@sha256:[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex ContainerImageDigestRegex();
}
