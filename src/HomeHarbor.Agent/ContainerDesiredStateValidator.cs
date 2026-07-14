using System.Globalization;
using System.Text;
using System.Text.Json;
using HomeHarbor.Tooling;

internal static partial class AgentProgram
{
    internal static string BuildValidatedContainerQuadlet(JsonElement item, string dataRoot)
    {
        var id = RequireGuid(item, "id", "container id");
        var familyId = RequireGuid(item, "familyId", "container familyId");
        var serviceName = "homeharbor-" + id.ToString("N");
        if (!item.TryGetProperty("definition", out var definition) || definition.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("container desired state is missing a structured definition");
        }

        RequireOnlyJsonProperties(
            definition,
            ["name", "image", "environment", "ports", "volumes", "command"],
            "container definition");
        var name = (JsonString(definition, "name") ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(name)) name = "Custom container";
        if (name.Length > 96 || name.Any(char.IsControl))
        {
            throw new InvalidOperationException("container name is invalid");
        }

        var image = JsonString(definition, "image") ?? string.Empty;
        ValidateContainerImage(image);
        var environment = ReadContainerEnvironment(definition);
        var ports = ReadContainerPorts(definition);
        var volumes = ReadContainerVolumes(definition, dataRoot, familyId, id);
        var command = ReadContainerCommand(definition);

        var builder = new StringBuilder();
        _ = builder.AppendLine("[Unit]");
        _ = builder.AppendLine("Description=HomeHarbor container " + name.Replace("%", "%%", StringComparison.Ordinal));
        _ = builder.AppendLine("After=network-online.target");
        _ = builder.AppendLine("Wants=network-online.target");
        _ = builder.AppendLine();
        _ = builder.AppendLine("[Container]");
        _ = builder.AppendLine("ContainerName=" + serviceName);
        _ = builder.AppendLine("Image=" + image);
        _ = builder.AppendLine("Pull=missing");
        _ = builder.AppendLine("NoNewPrivileges=true");
        _ = builder.AppendLine("UserNS=auto");
        foreach (var port in ports)
        {
            _ = builder.AppendLine(
                CultureInfo.InvariantCulture,
                $"PublishPort=127.0.0.1:{port.HostPort}:{port.TargetPort}{(port.Protocol == "udp" ? "/udp" : string.Empty)}");
        }
        foreach (var pair in environment)
        {
            _ = builder.AppendLine("Environment=" + QuoteQuadlet(pair.Key + "=" + pair.Value));
        }
        foreach (var volume in volumes)
        {
            _ = builder.AppendLine(
                "Volume=" + QuoteQuadlet(volume.HostPath + ":" + volume.ContainerPath + ":" + (volume.ReadOnly ? "ro,Z" : "rw,U,Z")));
        }
        if (command.Count > 0)
        {
            _ = builder.AppendLine("Exec=" + string.Join(' ', command.Select(QuoteQuadletArgument)));
        }
        _ = builder.AppendLine();
        _ = builder.AppendLine("[Service]");
        _ = builder.AppendLine("Environment=" + ContainerRuntimePaths.QuadletPodmanConfigEnvironment);
        _ = builder.AppendLine("Restart=on-failure");
        _ = builder.AppendLine("RestartSec=5");
        if (string.Equals(JsonString(item, "desiredState"), "running", StringComparison.Ordinal))
        {
            _ = builder.AppendLine();
            _ = builder.AppendLine("[Install]");
            _ = builder.AppendLine("WantedBy=default.target");
        }

        var rendered = builder.ToString();
        var supplied = JsonString(item, "quadlet") ?? string.Empty;
        if (!string.Equals(supplied, rendered, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("container Quadlet does not match the root-validated structured definition");
        }

        return rendered;
    }

    private static SortedDictionary<string, string> ReadContainerEnvironment(JsonElement definition)
    {
        if (!definition.TryGetProperty("environment", out var element) || element.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("container environment must be an object");
        }
        if (element.EnumerateObject().Take(65).Count() > 64)
        {
            throw new InvalidOperationException("container environment exceeds 64 entries");
        }

        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!IsEnvironmentKey(property.Name) || property.Value.ValueKind != JsonValueKind.String)
            {
                throw new InvalidOperationException("container environment entry is invalid: " + property.Name);
            }
            var value = property.Value.GetString() ?? string.Empty;
            if (value.Length > 4096 || value.Any(char.IsControl))
            {
                throw new InvalidOperationException("container environment value is invalid: " + property.Name);
            }
            result.Add(property.Name, value);
        }
        return result;
    }

    private static IReadOnlyList<ValidatedContainerPort> ReadContainerPorts(JsonElement definition)
    {
        if (!definition.TryGetProperty("ports", out var element) || element.ValueKind != JsonValueKind.Array || element.GetArrayLength() > 16)
        {
            throw new InvalidOperationException("container ports must be an array of at most 16 entries");
        }

        var result = new List<ValidatedContainerPort>();
        var hostPorts = new HashSet<int>();
        foreach (var item in element.EnumerateArray())
        {
            RequireOnlyJsonProperties(item, ["hostPort", "targetPort", "protocol"], "container port");
            var hostPort = RequiredJsonInt32(item, "hostPort", "container host port");
            var targetPort = RequiredJsonInt32(item, "targetPort", "container target port");
            var protocol = JsonString(item, "protocol") ?? string.Empty;
            if (hostPort is < 1024 or > 65535 || targetPort is < 1 or > 65535 ||
                protocol is not ("tcp" or "udp") || !hostPorts.Add(hostPort))
            {
                throw new InvalidOperationException("container port mapping is invalid");
            }
            result.Add(new ValidatedContainerPort(hostPort, targetPort, protocol));
        }
        return result;
    }

    private static IReadOnlyList<ValidatedContainerVolume> ReadContainerVolumes(
        JsonElement definition,
        string dataRoot,
        Guid familyId,
        Guid containerId)
    {
        if (!definition.TryGetProperty("volumes", out var element) || element.ValueKind != JsonValueKind.Array ||
            element.GetArrayLength() is < 1 or > 16)
        {
            throw new InvalidOperationException("container volumes must be an array of 1 to 16 entries");
        }

        var appRoot = Path.GetFullPath(Path.Combine(dataRoot, "apps", containerId.ToString("N")));
        _ = familyId;
        var result = new List<ValidatedContainerVolume>();
        var containerPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in element.EnumerateArray())
        {
            RequireOnlyJsonProperties(item, ["hostPath", "containerPath", "readOnly"], "container volume");
            var hostPathValue = JsonString(item, "hostPath") ?? string.Empty;
            if (hostPathValue.Any(char.IsControl) || hostPathValue.Contains(':', StringComparison.Ordinal))
            {
                throw new InvalidOperationException("container volume host path is invalid");
            }
            var hostPath = Path.GetFullPath(hostPathValue);
            var readOnly = JsonBoolean(item, "readOnly")
                ?? throw new InvalidOperationException("container volume readOnly must be a boolean");
            if (!string.Equals(hostPath, appRoot, StringComparison.Ordinal) || readOnly)
            {
                throw new InvalidOperationException("container volume must use the exact app data root read-write");
            }
            RequireDirectoryWithoutSymlinks(hostPath, "container volume host path");

            var containerPath = NormalizeValidatedContainerPath(JsonString(item, "containerPath"));
            if (!containerPaths.Add(containerPath))
            {
                throw new InvalidOperationException("container volume target path is duplicated: " + containerPath);
            }
            result.Add(new ValidatedContainerVolume(hostPath, containerPath, readOnly));
        }
        return result;
    }

    private static IReadOnlyList<string> ReadContainerCommand(JsonElement definition)
    {
        if (!definition.TryGetProperty("command", out var element) || element.ValueKind != JsonValueKind.Array || element.GetArrayLength() > 64)
        {
            throw new InvalidOperationException("container command must be an array of at most 64 entries");
        }

        var result = new List<string>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                throw new InvalidOperationException("container command arguments must be strings");
            }
            var value = item.GetString() ?? string.Empty;
            if (value.Length is < 1 or > 512 || value.Any(char.IsControl))
            {
                throw new InvalidOperationException("container command argument is invalid");
            }
            result.Add(value);
        }
        return result;
    }

    private static void ValidateContainerImage(string image)
    {
        const string marker = "@sha256:";
        var markerIndex = image.LastIndexOf(marker, StringComparison.Ordinal);
        var digest = markerIndex >= 0 ? image[(markerIndex + marker.Length)..] : string.Empty;
        var repository = markerIndex > 0 ? image[..markerIndex] : string.Empty;
        if (image.Length > 512 || markerIndex <= 0 || digest.Length != 64 ||
            image.StartsWith('-') || image.Any(char.IsWhiteSpace) || image.Any(char.IsControl) ||
            repository.LastIndexOf(':') > repository.LastIndexOf('/') ||
            digest.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw new InvalidOperationException("container image must be pinned with a lowercase sha256 digest");
        }

        ValidateContainerRepository(repository);
    }

    private static void ValidateContainerRepository(string repository)
    {
        if (repository.Length == 0 || repository.StartsWith('/') || repository.EndsWith('/') ||
            repository.Contains("//", StringComparison.Ordinal) ||
            repository.Any(character => character is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-' or '/' or ':')))
        {
            throw new InvalidOperationException("container image repository syntax is invalid");
        }

        var components = repository.Split('/');
        foreach (var component in components)
        {
            var name = component;
            var colon = component.IndexOf(':');
            if (colon >= 0)
            {
                if (component != components[0] || components.Length < 2 ||
                    component.LastIndexOf(':') != colon ||
                    !int.TryParse(component[(colon + 1)..], CultureInfo.InvariantCulture, out var port) ||
                    port is < 1 or > 65535)
                {
                    throw new InvalidOperationException("container image registry port is invalid");
                }
                name = component[..colon];
            }

            if (name.Length == 0 || !char.IsAsciiLetterOrDigit(name[0]) || !char.IsAsciiLetterOrDigit(name[^1]))
            {
                throw new InvalidOperationException("container image repository component is invalid");
            }
        }
    }

    private static string NormalizeValidatedContainerPath(string? value)
    {
        var path = value?.Trim() ?? string.Empty;
        if (!path.StartsWith('/') || path.Length == 0 || path.Contains(':', StringComparison.Ordinal) ||
            path.Any(char.IsControl) || path.Any(char.IsWhiteSpace) ||
            path.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(segment => segment is "." or ".."))
        {
            throw new InvalidOperationException("container volume target path is invalid");
        }
        return path == "/" ? path : path.TrimEnd('/');
    }

    private static void RequireOnlyJsonProperties(JsonElement element, IReadOnlyCollection<string> allowed, string label)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException(label + " must be an object");
        }
        foreach (var property in element.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
            {
                throw new InvalidOperationException(label + " contains forbidden property: " + property.Name);
            }
        }
    }

    private static int RequiredJsonInt32(JsonElement element, string property, string label)
        => element.TryGetProperty(property, out var value) && value.TryGetInt32(out var parsed)
            ? parsed
            : throw new InvalidOperationException(label + " must be an integer");

    private static bool IsEnvironmentKey(string value)
        => value.Length > 0 && (char.IsAsciiLetter(value[0]) || value[0] == '_') &&
           value.Skip(1).All(character => char.IsAsciiLetterOrDigit(character) || character == '_');

    private static string QuoteQuadlet(string value)
        => "\"" + value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("%", "%%", StringComparison.Ordinal) + "\"";

    private static string QuoteQuadletArgument(string value)
        => "\"" + value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("%", "%%", StringComparison.Ordinal)
            .Replace("$", "$$", StringComparison.Ordinal) + "\"";

    private sealed record ValidatedContainerPort(int HostPort, int TargetPort, string Protocol);

    private sealed record ValidatedContainerVolume(string HostPath, string ContainerPath, bool ReadOnly);
}
