using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HomeHarbor.Api.Data;
using HomeHarbor.Tooling;

namespace HomeHarbor.Api.Services;

public sealed partial class ManagedContainerSpecService(IHomeHarborStorageService storage) : IManagedContainerSpecService
{
    private const int MaxEnvironmentVariables = 64;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public ContainerDefinition Normalize(Guid familyId, Guid containerId, ContainerDefinitionRequest request)
    {
        var name = NormalizeName(request.Name);
        var image = NormalizeImage(request.Image);

        if (request.Privileged is true) throw new InvalidOperationException("Privileged containers are not allowed.");
        if (!string.IsNullOrWhiteSpace(request.PodmanArgs)) throw new InvalidOperationException("Raw PodmanArgs are not allowed.");
        if (request.Devices is { Count: > 0 }) throw new InvalidOperationException("Device mappings are not allowed.");
        if (request.Capabilities is { Count: > 0 }) throw new InvalidOperationException("Additional capabilities are not allowed.");
        if (!string.IsNullOrWhiteSpace(request.Network) &&
            !string.Equals(request.Network, "bridge", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Only the default rootless network is allowed.");
        }

        var environment = NormalizeEnvironment(request.Environment);
        var ports = NormalizePorts(request.Ports);
        var volumes = NormalizeVolumes(familyId, containerId, request.Volumes);
        var command = NormalizeCommand(request.Command);

        return new ContainerDefinition(
            name,
            image,
            environment,
            ports,
            volumes,
            command);
    }

    public string Serialize(ContainerDefinition definition)
        => JsonSerializer.Serialize(definition, JsonOptions);

    public ContainerDefinition Deserialize(string json)
        => JsonSerializer.Deserialize<ContainerDefinition>(json, JsonOptions)
            ?? new ContainerDefinition(
                string.Empty,
                string.Empty,
                new SortedDictionary<string, string>(StringComparer.Ordinal),
                [],
                [],
                []);

    public void EnsurePortsAvailable(
        ContainerDefinition definition,
        IEnumerable<ManagedContainerEntity> existingContainers,
        Guid? excludeContainerId = null)
    {
        var requested = definition.Ports
            .Select(port => (port.HostPort, Protocol: port.Protocol.ToLowerInvariant()))
            .ToHashSet();
        if (requested.Count == 0) return;

        foreach (var container in existingContainers)
        {
            if (container.DeletedAt is not null || container.Id == excludeContainerId) continue;

            ContainerDefinition existing;
            try
            {
                existing = Deserialize(container.DefinitionJson);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException(
                    $"Cannot validate host ports because stored container '{container.Name}' has an invalid definition.",
                    ex);
            }

            foreach (var port in existing.Ports)
            {
                if (requested.Contains((port.HostPort, port.Protocol.ToLowerInvariant())))
                {
                    throw new InvalidOperationException(
                        $"Host port {port.HostPort}/{port.Protocol.ToLowerInvariant()} is already used by container '{container.Name}'.");
                }
            }
        }
    }

    public string BuildQuadlet(ManagedContainerEntity container)
        => BuildQuadlet(container, Deserialize(container.DefinitionJson));

    public string BuildQuadlet(ManagedContainerEntity container, ContainerDefinition definition)
    {
        var expectedServiceName = "homeharbor-" + container.Id.ToString("N");
        if (!string.Equals(container.ServiceName, expectedServiceName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Container service identity does not match its id.");
        }

        var name = NormalizeName(definition.Name);
        var image = NormalizeImage(definition.Image);
        var environment = NormalizeEnvironment(definition.Environment);
        var ports = NormalizePorts(definition.Ports
            .Select(port => new ContainerPortRequest(port.HostPort, port.TargetPort, port.Protocol))
            .ToArray());
        var volumes = NormalizeVolumes(
            container.FamilyId,
            container.Id,
            definition.Volumes
                .Select(volume => new ContainerVolumeRequest(volume.HostPath, volume.ContainerPath, volume.ReadOnly))
                .ToArray());
        var command = NormalizeCommand(definition.Command);
        var familyRoot = NormalizeVolumeHostPath(Path.Combine(storage.DataRoot, "families", container.FamilyId.ToString("N")));
        var appRoot = NormalizeVolumeHostPath(Path.Combine(storage.DataRoot, "apps", container.Id.ToString("N")));

        var builder = new StringBuilder();
        _ = builder.AppendLine("[Unit]");
        _ = builder.AppendLine(CultureInfo.InvariantCulture, $"Description=HomeHarbor container {EscapeSystemdValue(name)}");
        _ = builder.AppendLine("After=network-online.target");
        _ = builder.AppendLine("Wants=network-online.target");
        _ = builder.AppendLine();
        _ = builder.AppendLine("[Container]");
        _ = builder.AppendLine(CultureInfo.InvariantCulture, $"ContainerName={container.ServiceName}");
        _ = builder.AppendLine(CultureInfo.InvariantCulture, $"Image={image}");
        _ = builder.AppendLine("Pull=missing");
        _ = builder.AppendLine("NoNewPrivileges=true");
        _ = builder.AppendLine("UserNS=auto");

        foreach (var port in ports)
        {
            var suffix = string.Equals(port.Protocol, "udp", StringComparison.OrdinalIgnoreCase) ? "/udp" : string.Empty;
            _ = builder.AppendLine(CultureInfo.InvariantCulture, $"PublishPort=127.0.0.1:{port.HostPort}:{port.TargetPort}{suffix}");
        }

        foreach (var item in environment.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            _ = builder.AppendLine(CultureInfo.InvariantCulture, $"Environment={QuoteSystemd($"{item.Key}={item.Value}")}");
        }

        foreach (var volume in volumes)
        {
            var hostPath = NormalizeVolumeHostPath(volume.HostPath);
            var containerPath = NormalizeContainerPath(volume.ContainerPath);
            if (!IsInside(hostPath, familyRoot) && !IsInside(hostPath, appRoot))
                throw new InvalidOperationException("Volume host paths must stay inside this family data root or this container app data root.");
            if (IsInside(hostPath, familyRoot))
                throw new InvalidOperationException("Family data volumes are unavailable until per-container read-only filesystem isolation is implemented.");
            if (!string.Equals(hostPath, appRoot, StringComparison.Ordinal) || volume.ReadOnly)
                throw new InvalidOperationException("Containers may mount only their private app data root read-write.");
            _ = builder.AppendLine(
                CultureInfo.InvariantCulture,
                $"Volume={QuoteSystemd($"{hostPath}:{containerPath}:{(volume.ReadOnly ? "ro,Z" : "rw,U,Z")}")}");
        }

        if (command.Count > 0)
        {
            _ = builder.AppendLine(CultureInfo.InvariantCulture, $"Exec={string.Join(' ', command.Select(QuoteSystemdArgument))}");
        }

        _ = builder.AppendLine();
        _ = builder.AppendLine("[Service]");
        _ = builder.AppendLine("Environment=" + ContainerRuntimePaths.QuadletPodmanConfigEnvironment);
        _ = builder.AppendLine("Restart=on-failure");
        _ = builder.AppendLine("RestartSec=5");
        if (string.Equals(container.DesiredState, "running", StringComparison.Ordinal))
        {
            _ = builder.AppendLine();
            _ = builder.AppendLine("[Install]");
            _ = builder.AppendLine("WantedBy=default.target");
        }
        return builder.ToString();
    }

    private static string NormalizeName(string? value)
    {
        var name = string.IsNullOrWhiteSpace(value) ? "Custom container" : value.Trim();
        if (name.Length > 96) throw new InvalidOperationException("Container name is too long.");
        if (name.Any(char.IsControl)) throw new InvalidOperationException("Container name cannot contain control characters.");
        return name;
    }

    private static string NormalizeImage(string? value)
    {
        var image = value?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(image)) throw new InvalidOperationException("Image is required.");
        if (image.Length > 512) throw new InvalidOperationException("Image is too long.");
        if (image.StartsWith('-') || image.Any(char.IsWhiteSpace) || image.Any(char.IsControl))
        {
            throw new InvalidOperationException("Image must be a container reference without whitespace.");
        }
        if (!ContainerImageDigestRegex().IsMatch(image))
        {
            throw new InvalidOperationException(
                "Image must be an untagged repository reference pinned with @sha256:<64 lowercase hex characters>.");
        }
        RefuseTagWithDigest(image);
        ValidateImageRepository(image[..image.LastIndexOf("@sha256:", StringComparison.Ordinal)]);

        return image;
    }

    private static void ValidateImageRepository(string repository)
    {
        if (repository.Length == 0 || repository.StartsWith('/') || repository.EndsWith('/') ||
            repository.Contains("//", StringComparison.Ordinal) ||
            repository.Any(character => character is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-' or '/' or ':')))
        {
            throw new InvalidOperationException("Image repository contains unsupported characters or path syntax.");
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
                    throw new InvalidOperationException("Image repository may use a numeric registry port only in its first component.");
                }
                name = component[..colon];
            }

            if (name.Length == 0 || !char.IsAsciiLetterOrDigit(name[0]) || !char.IsAsciiLetterOrDigit(name[^1]))
            {
                throw new InvalidOperationException("Image repository components must start and end with a lowercase letter or digit.");
            }
        }
    }

    private static IReadOnlyDictionary<string, string> NormalizeEnvironment(IReadOnlyDictionary<string, string>? environment)
    {
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (environment is null) return result;
        if (environment.Count > MaxEnvironmentVariables)
            throw new InvalidOperationException($"A container may define at most {MaxEnvironmentVariables} environment variables.");

        foreach (var (rawKey, rawValue) in environment)
        {
            var key = rawKey.Trim();
            if (!EnvironmentKeyRegex().IsMatch(key))
                throw new InvalidOperationException($"Environment variable '{rawKey}' is not allowed.");
            if (rawValue is null || rawValue.Any(char.IsControl))
                throw new InvalidOperationException("Environment values cannot contain control characters.");
            if (rawValue.Length > 4096) throw new InvalidOperationException("Environment value is too long.");
            result[key] = rawValue;
        }

        return result;
    }

    private static IReadOnlyList<ContainerPort> NormalizePorts(IReadOnlyList<ContainerPortRequest>? ports)
    {
        if (ports is null) return [];
        if (ports.Count > 16) throw new InvalidOperationException("Too many port mappings.");

        var result = new List<ContainerPort>();
        var usedHostPorts = new HashSet<int>();
        foreach (var port in ports)
        {
            if (port.HostPort is < 1024 or > 65535)
                throw new InvalidOperationException("Rootless containers can only publish host ports from 1024 to 65535.");
            if (port.ContainerPort is < 1 or > 65535)
                throw new InvalidOperationException("Container ports must be between 1 and 65535.");
            var protocol = string.IsNullOrWhiteSpace(port.Protocol) ? "tcp" : port.Protocol.Trim().ToLowerInvariant();
            if (protocol is not ("tcp" or "udp")) throw new InvalidOperationException("Port protocol must be tcp or udp.");
            if (!usedHostPorts.Add(port.HostPort)) throw new InvalidOperationException("Duplicate host ports are not allowed.");
            result.Add(new ContainerPort(port.HostPort, port.ContainerPort, protocol));
        }

        return result;
    }

    private IReadOnlyList<ContainerVolume> NormalizeVolumes(
        Guid familyId,
        Guid containerId,
        IReadOnlyList<ContainerVolumeRequest>? volumes)
    {
        var appRoot = NormalizeVolumeHostPath(Path.Combine(storage.DataRoot, "apps", containerId.ToString("N")));

        if (volumes is null || volumes.Count == 0)
        {
            return [new ContainerVolume(appRoot, "/data", false)];
        }

        if (volumes.Count > 16) throw new InvalidOperationException("Too many volume mappings.");
        var familyRoot = NormalizeVolumeHostPath(Path.Combine(storage.DataRoot, "families", familyId.ToString("N")));
        var result = new List<ContainerVolume>();

        foreach (var volume in volumes)
        {
            if (volume.HostPath is not null && volume.HostPath.Any(char.IsControl))
                throw new InvalidOperationException("Volume host paths cannot contain control characters.");

            var hostPath = NormalizeVolumeHostPath(string.IsNullOrWhiteSpace(volume.HostPath)
                ? appRoot
                : volume.HostPath.Trim());
            var containerPath = NormalizeContainerPath(volume.ContainerPath);
            if (!IsInside(hostPath, familyRoot) && !IsInside(hostPath, appRoot))
                throw new InvalidOperationException("Volume host paths must stay inside this family data root or this container app data root.");
            if (IsInside(hostPath, familyRoot))
                throw new InvalidOperationException("Family data volumes are unavailable until per-container read-only filesystem isolation is implemented.");
            if (!string.Equals(hostPath, appRoot, StringComparison.Ordinal))
                throw new InvalidOperationException("Containers may mount only their private app data root, not host subdirectories.");
            if (volume.ReadOnly)
                throw new InvalidOperationException("The private app data root must be mounted read-write.");
            var createdHostPath = NormalizeVolumeHostPath(hostPath);
            if (!string.Equals(createdHostPath, appRoot, StringComparison.Ordinal))
                throw new InvalidOperationException("Container app data root changed while preparing the volume.");
            result.Add(new ContainerVolume(createdHostPath, containerPath, volume.ReadOnly));
        }

        return result;
    }

    private static IReadOnlyList<string> NormalizeCommand(IReadOnlyList<string>? command)
    {
        if (command is null) return [];
        if (command.Count > 64) throw new InvalidOperationException("Command has too many arguments.");

        var result = new List<string>();
        foreach (var arg in command)
        {
            if (string.IsNullOrEmpty(arg)) throw new InvalidOperationException("Command arguments cannot be empty.");
            if (arg.Any(char.IsControl))
                throw new InvalidOperationException("Command arguments cannot contain control characters.");
            if (arg.Length > 512) throw new InvalidOperationException("Command argument is too long.");
            result.Add(arg);
        }

        return result;
    }

    private static string NormalizeContainerPath(string? value)
    {
        var path = value?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("Container path is required.");
        if (!path.StartsWith('/')) throw new InvalidOperationException("Container path must be absolute.");
        if (path.Any(char.IsControl))
        {
            throw new InvalidOperationException("Container paths cannot contain control characters.");
        }
        if (path.Contains(':', StringComparison.Ordinal)) throw new InvalidOperationException("Container paths cannot contain colon separators.");
        if (path.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(segment => segment is "." or ".."))
        {
            throw new InvalidOperationException("Container path traversal segments are not allowed.");
        }
        if (path.Any(char.IsWhiteSpace))
        {
            throw new InvalidOperationException("Container paths cannot contain whitespace.");
        }

        return path.TrimEnd('/');
    }

    private static string NormalizeVolumeHostPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException("Volume host path is required.");
        ValidateVolumeHostPath(value);
        var resolved = ResolvePathPreservingMissingLeaf(value);
        ValidateVolumeHostPath(resolved);
        return resolved;
    }

    private static void ValidateVolumeHostPath(string value)
    {
        if (value.Any(char.IsControl)) throw new InvalidOperationException("Volume host paths cannot contain control characters.");
        if (value.Contains(':', StringComparison.Ordinal)) throw new InvalidOperationException("Volume host paths cannot contain colon separators.");
    }

    private static string ResolvePathPreservingMissingLeaf(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(root)) return fullPath;

        var resolved = root;
        var segments = fullPath[root.Length..]
            .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);

        foreach (var segment in segments)
        {
            var next = Path.Combine(resolved, segment);
            var target = ResolveFinalLinkTarget(next);
            resolved = target is null ? next : Path.GetFullPath(target.FullName);
        }

        return Path.GetFullPath(resolved);
    }

    private static FileSystemInfo? ResolveFinalLinkTarget(string path)
    {
        var isSymbolicLink = IsSymbolicLink(path);
        if (!isSymbolicLink && !File.Exists(path) && !Directory.Exists(path))
        {
            return null;
        }

        try
        {
            FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
            var target = info.ResolveLinkTarget(returnFinalTarget: true);
            if (target is null && isSymbolicLink)
                throw new InvalidOperationException("Volume host path contains a broken symbolic link.");
            return target;
        }
        catch (FileNotFoundException) when (!isSymbolicLink)
        {
            return null;
        }
        catch (DirectoryNotFoundException) when (!isSymbolicLink)
        {
            return null;
        }
        catch (IOException exception)
        {
            throw new InvalidOperationException("Volume host path contains an invalid symbolic link.", exception);
        }
    }

    private static bool IsSymbolicLink(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }

    private static bool IsInside(string candidate, string root)
    {
        var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar);
        if (normalizedRoot.Length == 0)
        {
            normalizedRoot = Path.DirectorySeparatorChar.ToString();
        }

        return candidate.Equals(normalizedRoot, StringComparison.Ordinal) ||
            candidate.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }


    private static string EscapeSystemdValue(string value)
        => value.Replace("%", "%%", StringComparison.Ordinal).Trim();

    private static string QuoteSystemd(string value)
        => "\"" + value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("%", "%%", StringComparison.Ordinal) + "\"";

    private static string QuoteSystemdArgument(string value)
        => "\"" + value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("%", "%%", StringComparison.Ordinal)
            .Replace("$", "$$", StringComparison.Ordinal) + "\"";

    private static void RefuseTagWithDigest(string image)
    {
        var digestSeparator = image.LastIndexOf("@sha256:", StringComparison.Ordinal);
        var repository = image[..digestSeparator];
        var lastSlash = repository.LastIndexOf('/');
        if (repository.LastIndexOf(':') > lastSlash)
        {
            throw new InvalidOperationException(
                "Container image references cannot combine a tag with a digest; use repository@sha256:<digest>.");
        }
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex EnvironmentKeyRegex();

    [GeneratedRegex("@sha256:[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex ContainerImageDigestRegex();
}

public sealed record ContainerDefinition(
    string Name,
    string Image,
    IReadOnlyDictionary<string, string> Environment,
    IReadOnlyList<ContainerPort> Ports,
    IReadOnlyList<ContainerVolume> Volumes,
    IReadOnlyList<string> Command);

public sealed record ContainerPort(int HostPort, int TargetPort, string Protocol);

public sealed record ContainerVolume(string HostPath, string ContainerPath, bool ReadOnly);

public sealed record ContainerDefinitionRequest(
    string? Name,
    string? Image,
    IReadOnlyDictionary<string, string>? Environment,
    IReadOnlyList<ContainerPortRequest>? Ports,
    IReadOnlyList<ContainerVolumeRequest>? Volumes,
    IReadOnlyList<string>? Command,
    bool? Privileged,
    string? Network,
    IReadOnlyList<string>? Devices,
    IReadOnlyList<string>? Capabilities,
    string? PodmanArgs);

public sealed record ContainerPortRequest(int HostPort, int ContainerPort, string? Protocol);

public sealed record ContainerVolumeRequest(string? HostPath, string? ContainerPath, bool ReadOnly);
