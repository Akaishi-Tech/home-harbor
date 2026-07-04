using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HomeHarbor.Tooling;

namespace HomeHarbor.Api.Services;

public sealed class AppRuntimeCatalog : IAppRuntimeCatalog, IDisposable
{
    private const string DefaultAppStoreBaseUrl = "https://akaishi-tech.github.io/home-harbor/apps";
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
            var indexJson = await ReadUriTextAsync(indexUrl, cancellationToken);
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
                    var manifestJson = await ReadUriTextAsync(entry.ManifestUrl, cancellationToken);
                    var actualSha = Sha256Hex(manifestJson);
                    if (!string.Equals(actualSha, entry.ManifestSha256, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger?.LogWarning("Skipping app {AppKey}; manifest hash mismatch.", entry.AppKey);
                        continue;
                    }

                    var manifest = await verifier.VerifyAppManifestJsonAsync(manifestJson, publicKey, entry.ManifestUrl, cancellationToken);
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

                    templates[manifest.AppKey] = ManagedAppTemplate.FromManifest(manifest, source: "remote");
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

    private async Task<string> ReadUriTextAsync(string uriText, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(uriText, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException("app store URL must be absolute: " + uriText);
        }

        if (uri.Scheme == Uri.UriSchemeFile)
        {
            return await File.ReadAllTextAsync(uri.LocalPath, cancellationToken);
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("app store URL scheme is not allowed: " + uri.Scheme);
        }

        return await _http.GetStringAsync(uri, cancellationToken);
    }

    private static IReadOnlyList<ManagedAppTemplate> BuildBuiltInTemplates()
    {
        return
        [
            ManagedAppTemplate.FromManifest(BuiltInContainer(
                "vaultwarden",
                "Password Vault",
                "家庭密码库",
                "Family password vault",
                "productivity",
                "docker.io/vaultwarden/server:latest",
                8081,
                recommendedInSetup: true)),
            ManagedAppTemplate.FromManifest(BuiltInContainer(
                "jellyfin",
                "Media Library",
                "媒体库",
                "Movies, shows, and music",
                "media",
                "docker.io/jellyfin/jellyfin:latest",
                8096,
                recommendedInSetup: true)),
            ManagedAppTemplate.FromManifest(BuiltInContainer(
                "syncthing",
                "Device Sync",
                "设备同步",
                "Peer device synchronization",
                "sync",
                "docker.io/syncthing/syncthing:latest",
                8384,
                recommendedInSetup: true)),
            ManagedAppTemplate.FromManifest(BuiltInContainer(
                "immich",
                "Photo Library",
                "照片库",
                "Photo backup and timeline",
                "photos",
                "ghcr.io/immich-app/immich-server:release",
                2283,
                recommendedInSetup: true))
        ];
    }

    private static HomeHarborAppManifest BuiltInContainer(
        string appKey,
        string displayName,
        string title,
        string description,
        string category,
        string image,
        int port,
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
                ports = new[] { new { hostPort = port, containerPort = port, protocol = "tcp" } },
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
    string ManifestJson,
    HomeHarborAppManifest Manifest)
{
    private static readonly JsonSerializerOptions ManifestJsonOptions = new(JsonSerializerDefaults.Web);

    public static ManagedAppTemplate FromManifest(HomeHarborAppManifest manifest, string? source = null)
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
                manifest.RecommendedInSetup,
                RequiresReboot: true,
                manifest.VisibleRoles,
                Available: true,
                UnavailableReason: string.Empty,
                source ?? manifest.Source,
                system.Commands,
                system.HotCheck,
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
