using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HomeHarbor.Tooling;

namespace HomeHarbor.Api.Services;

public sealed class AppRuntimeCatalog : IAppRuntimeCatalog, IDisposable
{
    private const string DefaultAppStoreBaseUrl = "https://akaishi-tech.github.io/home-harbor/apps";
    private const long MaxStoreIndexBytes = 1024 * 1024;
    private const long MaxAppManifestBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly ILogger<AppRuntimeCatalog>? _logger;
    private readonly bool _remoteEnabled;
    private readonly SemaphoreSlim _cacheLock = new(1, 1);
    private IReadOnlyList<ManagedAppTemplate>? _cached;
    private DateTimeOffset _cacheUntil;

    public AppRuntimeCatalog()
        : this(new HttpClient { Timeout = TimeSpan.FromSeconds(5) }, null, remoteEnabled: false)
    {
    }

    public AppRuntimeCatalog(HttpClient http, ILogger<AppRuntimeCatalog>? logger = null)
        : this(http, logger, remoteEnabled: true)
    {
    }

    private AppRuntimeCatalog(HttpClient http, ILogger<AppRuntimeCatalog>? logger, bool remoteEnabled)
    {
        _http = http;
        _logger = logger;
        _remoteEnabled = remoteEnabled;
    }

    public IReadOnlyList<ManagedAppTemplate> List(string? role = null)
        => BuildBuiltInTemplates().Where(template => template.IsVisibleTo(role)).ToArray();

    public ManagedAppTemplate? Find(string appKey)
        => BuildBuiltInTemplates().FirstOrDefault(t => string.Equals(t.AppKey, appKey, StringComparison.OrdinalIgnoreCase));

    public async Task<IReadOnlyList<ManagedAppTemplate>> ListAsync(string? role = null, CancellationToken cancellationToken = default)
        => (await LoadTemplatesAsync(cancellationToken)).Where(template => template.IsVisibleTo(role)).ToArray();

    public async Task<ManagedAppTemplate?> FindAsync(string appKey, CancellationToken cancellationToken = default)
        => (await LoadTemplatesAsync(cancellationToken))
            .FirstOrDefault(t => string.Equals(t.AppKey, appKey, StringComparison.OrdinalIgnoreCase));

    private async Task<IReadOnlyList<ManagedAppTemplate>> LoadTemplatesAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        if (_cached is not null && _cacheUntil > now)
        {
            return _cached;
        }

        await _cacheLock.WaitAsync(cancellationToken);
        try
        {
            now = DateTimeOffset.UtcNow;
            if (_cached is not null && _cacheUntil > now)
            {
                return _cached;
            }

            var templates = BuildBuiltInTemplates()
                .ToDictionary(template => template.AppKey, StringComparer.OrdinalIgnoreCase);

            if (_remoteEnabled && !Env.Flag("HOMEHARBOR_APP_STORE_DISABLE_REMOTE"))
            {
                await MergeRemoteTemplatesAsync(templates, cancellationToken);
            }

            _cached = templates.Values
                .OrderBy(template => template.Category, StringComparer.Ordinal)
                .ThenBy(template => template.DisplayName, StringComparer.Ordinal)
                .ToArray();
            _cacheUntil = now.AddSeconds(Math.Max(5, Env.Int("HOMEHARBOR_APP_STORE_CACHE_SECONDS", 300)));
            return _cached;
        }
        finally
        {
            _ = _cacheLock.Release();
        }
    }

    private async Task MergeRemoteTemplatesAsync(
        IDictionary<string, ManagedAppTemplate> templates,
        CancellationToken cancellationToken)
    {
        var publicKey = Env.String("HOMEHARBOR_RELEASE_PUBLIC_KEY", "/etc/homeharbor/release.pub.pem");
        if (!File.Exists(publicKey))
        {
            if (_logger?.IsEnabled(LogLevel.Debug) == true)
            {
                _logger.LogDebug("Skipping remote app store because release public key is missing: {PublicKey}", publicKey);
            }
            return;
        }

        try
        {
            var indexUrl = StoreIndexUrl();
            var fileRoot = AppStoreFileRoot(indexUrl);
            var indexUri = BoundedUriFetcher.ValidateUri(indexUrl, allowedFileRoot: fileRoot, label: "app store index URL");
            var indexJson = await ReadUriTextAsync(
                indexUrl,
                MaxStoreIndexBytes,
                sameOriginAs: null,
                fileRoot,
                "app store index",
                cancellationToken);
            var verifier = new HomeHarborAppManifestVerifier();
            var index = await verifier.VerifyStoreIndexJsonAsync(indexJson, publicKey, cancellationToken);
            var channel = OtaRuntime.Channel();
            if (!string.Equals(index.Channel, channel, StringComparison.Ordinal))
            {
                _logger?.LogWarning("Skipping remote app store index for channel {IndexChannel}; current channel is {Channel}.", index.Channel, channel);
                return;
            }

            foreach (var entry in index.Apps)
            {
                if (string.Equals(entry.AppKey, "zfs-utils", StringComparison.OrdinalIgnoreCase))
                {
                    _logger?.LogDebug("Skipping zfs-utils app-store entry because ZFS tools are delivered as a kernel addon.");
                    continue;
                }

                try
                {
                    var manifestJson = await ReadUriTextAsync(
                        entry.ManifestUrl,
                        MaxAppManifestBytes,
                        indexUri,
                        fileRoot,
                        $"app manifest {entry.AppKey}",
                        cancellationToken);
                    var actualSha = Sha256Hex(manifestJson);
                    if (!string.Equals(actualSha, entry.ManifestSha256, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger?.LogWarning("Skipping app {AppKey}; manifest hash mismatch.", entry.AppKey);
                        continue;
                    }

                    var manifest = await verifier.VerifyAppManifestJsonAsync(manifestJson, publicKey, entry.ManifestUrl, cancellationToken);
                    if (!string.Equals(manifest.AppKey, entry.AppKey, StringComparison.Ordinal) ||
                        !string.Equals(manifest.Version, entry.Version, StringComparison.Ordinal))
                    {
                        _logger?.LogWarning(
                            "Skipping app-store entry {EntryAppKey}; signed manifest identity {ManifestAppKey}@{ManifestVersion} does not match index version {EntryVersion}.",
                            entry.AppKey,
                            manifest.AppKey,
                            manifest.Version,
                            entry.Version);
                        continue;
                    }

                    if (!string.Equals(manifest.Channel, channel, StringComparison.Ordinal))
                    {
                        _logger?.LogWarning("Skipping app {AppKey}; manifest channel {ManifestChannel} does not match {Channel}.", manifest.AppKey, manifest.Channel, channel);
                        continue;
                    }
                    if (string.Equals(manifest.AppKey, "zfs-utils", StringComparison.OrdinalIgnoreCase))
                    {
                        _logger?.LogDebug("Skipping zfs-utils app manifest because ZFS tools are delivered as a kernel addon.");
                        continue;
                    }

                    if (manifest.Install is HomeHarborSystemAppInstall systemInstall)
                    {
                        _ = BoundedUriFetcher.ValidateUri(
                            systemInstall.ManifestUrl,
                            indexUri,
                            fileRoot,
                            $"system app manifest URL for {manifest.AppKey}");
                    }

                    templates[manifest.AppKey] = ManagedAppTemplate.FromManifest(
                        manifest,
                        source: "remote",
                        signedManifestJson: manifestJson);
                }
                catch (Exception ex) when (ex is IOException or HttpRequestException or InvalidOperationException or JsonException or FormatException or CryptographicException)
                {
                    _logger?.LogWarning(ex, "Skipping remote app store entry {AppKey}.", entry.AppKey);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or InvalidOperationException or JsonException or FormatException or CryptographicException)
        {
            _logger?.LogWarning(ex, "Remote app store could not be loaded; using built-in catalog.");
        }
    }

    private Task<string> ReadUriTextAsync(
        string uriText,
        long maxBytes,
        Uri? sameOriginAs,
        string? fileRoot,
        string label,
        CancellationToken cancellationToken)
        => BoundedUriFetcher.ReadUtf8TextAsync(
            _http,
            uriText,
            maxBytes,
            sameOriginAs,
            fileRoot,
            label,
            cancellationToken);

    private static IReadOnlyList<ManagedAppTemplate> BuildBuiltInTemplates()
    {
        var vaultwarden = ManagedAppTemplate.FromManifest(BuiltInContainer(
            "vaultwarden",
            "Password Vault",
            "家庭密码库",
            "Family password vault",
            "productivity",
            "docker.io/vaultwarden/server@sha256:d626d04934cd1192ad8ced1adb975099fca78cec33ab467d2d3c923cde7f3b0c",
            hostPort: 8081,
            containerPort: 80,
            recommendedInSetup: false)) with
        {
            Available = false,
            UnavailableReason = "Secure TLS ingress is not provisioned for the password vault yet."
        };
        var jellyfin = ManagedAppTemplate.FromManifest(BuiltInContainer(
            "jellyfin",
            "Media Library",
            "媒体库",
            "Movies, shows, and music",
            "media",
            "docker.io/jellyfin/jellyfin@sha256:aefb67e6a7ff1debdd154a78a7bbb780fd0c873d8639210a7f6a2016ad2b35db",
            hostPort: 8096,
            containerPort: 8096,
            recommendedInSetup: false)) with
        {
            Available = false,
            UnavailableReason = "Media and configuration mounts are not safely isolated yet."
        };
        var syncthing = ManagedAppTemplate.FromManifest(BuiltInContainer(
            "syncthing",
            "Device Sync",
            "设备同步",
            "Peer device synchronization",
            "sync",
            "docker.io/syncthing/syncthing@sha256:4464f4161dd0251e20d46bb3aec83363db75d80cef1abdd5d5fd4054b04a004d",
            hostPort: 8384,
            containerPort: 8384,
            recommendedInSetup: false)) with
        {
            Available = false,
            UnavailableReason = "Syncthing configuration and family-data isolation are not implemented yet."
        };
        var immich = ManagedAppTemplate.FromManifest(BuiltInContainer(
            "immich",
            "Photo Library",
            "照片库",
            "Photo backup and timeline",
            "photos",
            "ghcr.io/immich-app/immich-server@sha256:14390f3dc9512dc3273b12ccee6363d9be16c388699abc3f3fe0498bb9829937",
            hostPort: 2283,
            containerPort: 2283,
            recommendedInSetup: false)) with
        {
            Available = false,
            UnavailableReason = "The required PostgreSQL, Redis, and media-storage services are not provisioned yet."
        };

        return
        [
            vaultwarden,
            jellyfin,
            syncthing,
            immich
        ];
    }

    private static HomeHarborAppManifest BuiltInContainer(
        string appKey,
        string displayName,
        string title,
        string description,
        string category,
        string image,
        int hostPort,
        int containerPort,
        bool recommendedInSetup)
    {
        var manifest = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            kind = HomeHarborAppManifestVerifier.AppKind,
            appKey,
            version = "latest",
            channel = OtaRuntime.Channel(),
            displayName,
            title,
            description,
            category,
            recommendedInSetup,
            visibleRoles = Array.Empty<string>(),
            install = new
            {
                type = "container",
                image,
                ports = new[] { new { hostPort, containerPort, protocol = "tcp" } },
                environment = new Dictionary<string, string>(),
                volumes = Array.Empty<object>(),
                command = Array.Empty<string>()
            }
        }, JsonOptions);
        using var doc = JsonDocument.Parse(manifest);
        return HomeHarborAppManifestVerifier.ParseTrustedAppManifest(doc.RootElement);
    }

    private static string StoreIndexUrl()
        => Environment.GetEnvironmentVariable("HOMEHARBOR_APP_STORE_INDEX_URL")
           ?? $"{AppStoreBaseUrl()}/index.json";

    private static string AppStoreBaseUrl()
        => (Environment.GetEnvironmentVariable("HOMEHARBOR_APP_STORE_BASE_URL")
            ?? DefaultAppStoreBaseUrl).TrimEnd('/');

    private static string? AppStoreFileRoot(string indexUrl)
    {
        var configured = Environment.GetEnvironmentVariable("HOMEHARBOR_APP_STORE_FILE_ROOT");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(configured);
        }

        return Uri.TryCreate(indexUrl, UriKind.Absolute, out var indexUri) && indexUri.Scheme == Uri.UriSchemeFile
            ? Path.GetDirectoryName(Path.GetFullPath(indexUri.LocalPath))
            : null;
    }

    private static string Sha256Hex(string text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    public void Dispose()
    {
        _cacheLock.Dispose();
    }
}

public sealed record ManagedAppTemplate(
    string AppKey,
    string DisplayName,
    string Title,
    string Description,
    string Category,
    string Kind,
    string InstallMode,
    string Image,
    int? Port,
    string Version,
    string ManifestUrl,
    bool RecommendedInSetup,
    bool RequiresReboot,
    IReadOnlyList<string> VisibleRoles,
    bool Available,
    string UnavailableReason,
    string Source,
    IReadOnlyList<string> Commands,
    HomeHarborAppHotCheck? HotCheck,
    string SignedManifestJson,
    string ManifestJson,
    HomeHarborAppManifest Manifest)
{
    private static readonly JsonSerializerOptions ManifestJsonOptions = new(JsonSerializerDefaults.Web);

    public static ManagedAppTemplate FromManifest(
        HomeHarborAppManifest manifest,
        string? source = null,
        string? signedManifestJson = null)
    {
        var kind = manifest.Install.Type;
        return manifest.Install switch
        {
            HomeHarborContainerAppInstall container => new(
                manifest.AppKey,
                manifest.DisplayName,
                manifest.Title,
                manifest.Description,
                manifest.Category,
                "container",
                "container-desired-state",
                container.Image,
                container.Ports.Count > 0 ? container.Ports[0].HostPort : null,
                manifest.Version,
                string.Empty,
                manifest.RecommendedInSetup,
                RequiresReboot: false,
                manifest.VisibleRoles,
                Available: true,
                UnavailableReason: string.Empty,
                source ?? manifest.Source,
                Commands: [],
                HotCheck: null,
                SignedManifestJson: signedManifestJson ?? string.Empty,
                ToManifestJson(manifest),
                manifest),
            HomeHarborSystemAppInstall system => new(
                manifest.AppKey,
                manifest.DisplayName,
                manifest.Title,
                manifest.Description,
                manifest.Category,
                "system",
                "signed-system-payload",
                string.Empty,
                null,
                manifest.Version,
                system.ManifestUrl,
                RecommendedInSetup: false,
                RequiresReboot: true,
                manifest.VisibleRoles,
                Available: false,
                UnavailableReason: "Persistent system-app activation and boot-time wrapper reconstruction are not implemented yet.",
                source ?? manifest.Source,
                system.Commands,
                system.HotCheck,
                SignedManifestJson: signedManifestJson ?? string.Empty,
                ToManifestJson(manifest),
                manifest),
            _ => throw new InvalidOperationException("Unknown HHAF install type: " + kind)
        };
    }

    public bool IsVisibleTo(string? role)
        => VisibleRoles.Count == 0 || VisibleRoles.Any(visibleRole => string.Equals(visibleRole, role, StringComparison.OrdinalIgnoreCase));

    public static string ToManifestJson(HomeHarborAppManifest manifest)
        => JsonSerializer.Serialize(ToManifestDto(manifest), ManifestJsonOptions);

    private static object ToManifestDto(HomeHarborAppManifest manifest)
        => new
        {
            manifest.SchemaVersion,
            manifest.Kind,
            manifest.AppKey,
            manifest.Version,
            manifest.Channel,
            manifest.DisplayName,
            manifest.Title,
            manifest.Description,
            manifest.Category,
            manifest.RecommendedInSetup,
            manifest.VisibleRoles,
            Install = manifest.Install switch
            {
                HomeHarborContainerAppInstall container => (object)new
                {
                    container.Type,
                    container.Image,
                    container.Ports,
                    container.Environment,
                    container.Volumes,
                    container.Command
                },
                HomeHarborSystemAppInstall system => (object)new
                {
                    system.Type,
                    system.Mode,
                    system.ManifestUrl,
                    system.Commands,
                    system.HotCheck,
                    AutoInstall = new { WhenKernelModulesPresent = system.AutoInstallWhenKernelModulesPresent }
                },
                _ => throw new InvalidOperationException("Unknown HHAF install type: " + manifest.Install.Type)
            }
        };
}
