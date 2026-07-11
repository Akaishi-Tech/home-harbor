using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;

namespace HomeHarbor.FullE2E.Tests.Infrastructure;

internal sealed class HomeHarborApiScenario(Func<HttpClient> clientFactory, string setupBootstrapCode) : IDisposable
{
    private readonly HttpClient _api = clientFactory();
    private readonly HttpClient _anonymous = clientFactory();
    private readonly HttpClient _proxy = clientFactory();

    private Guid _familyId;
    private Guid _setupDeviceId;
    private Guid _registeredDeviceId;
    private string _initialDavUsername = string.Empty;
    private string _initialDavToken = string.Empty;
    private string _photosDavUsername = string.Empty;
    private string _photosDavToken = string.Empty;
    private Guid _backupTargetId;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await VerifyInitialBootAndSetupAsync(cancellationToken);
        await VerifyAuthenticationAsync(cancellationToken);
        await VerifyFamilyMembersAndDevicesAsync(cancellationToken);
        await VerifyWebDavTokensAsync(cancellationToken);
        await VerifyWebDavAndMediaAsync(cancellationToken);
        await VerifyVaultAndSyncAsync(cancellationToken);
        await VerifyAppsNetworkingBackupAndRecoveryAsync(cancellationToken);
        await VerifyRemoteSecurityOtaAndOverviewAsync(cancellationToken);
    }

    public void Dispose()
    {
        _api.Dispose();
        _anonymous.Dispose();
        _proxy.Dispose();
    }

    private async Task VerifyInitialBootAndSetupAsync(CancellationToken cancellationToken)
    {
        var health = await GetObjectAsync(_anonymous, "/api/system/health", cancellationToken);
        Assert.AreEqual("ok", health["status"]!.GetValue<string>());
        Assert.AreEqual(0, health["familyCount"]!.GetValue<int>());

        using var proxy = await _proxy.GetAsync("/", cancellationToken);
        Assert.AreEqual(HttpStatusCode.OK, proxy.StatusCode);

        var before = await GetObjectAsync(_anonymous, "/api/setup", cancellationToken);
        Assert.IsFalse(before["initialized"]!.GetValue<bool>());

        var pairing = await GetObjectAsync(_anonymous, "/api/setup/pairing", cancellationToken);
        Assert.IsTrue(pairing["codeRequired"]!.GetValue<bool>());

        using var qr = await _anonymous.GetAsync("/api/setup/pairing.svg", cancellationToken);
        Assert.AreEqual(HttpStatusCode.NotFound, qr.StatusCode);

        var setup = await PostObjectAsync(_anonymous, "/api/setup", new
        {
            familyName = "Harbor E2E Home",
            ownerDisplayName = "Owner",
            ownerPassword = "owner-pass-e2e-2026",
            deviceName = "E2E Browser",
            pairingCode = setupBootstrapCode
        }, cancellationToken);

        _familyId = Guid.Parse(setup["family"]!["id"]!.GetValue<string>());
        _setupDeviceId = Guid.Parse(setup["device"]!["id"]!.GetValue<string>());
        _initialDavUsername = setup["webDav"]!["username"]!.GetValue<string>();
        _initialDavToken = setup["webDav"]!["token"]!.GetValue<string>();
        _api.DefaultRequestHeaders.Authorization = Bearer(setup["auth"]!["accessToken"]!.GetValue<string>());

        using var conflict = await _anonymous.PostAsJsonAsync("/api/setup", new { familyName = "Second" }, cancellationToken);
        Assert.AreEqual(HttpStatusCode.Conflict, conflict.StatusCode);
    }

    private async Task VerifyAuthenticationAsync(CancellationToken cancellationToken)
    {
        using var anonymousOverview = await _anonymous.GetAsync("/api/home/overview", cancellationToken);
        Assert.AreEqual(HttpStatusCode.Unauthorized, anonymousOverview.StatusCode);

        using var badLogin = await _anonymous.PostAsJsonAsync("/api/identity/login", new
        {
            displayName = "Owner",
            password = "bad-password"
        }, cancellationToken);
        Assert.AreEqual(HttpStatusCode.Unauthorized, badLogin.StatusCode);

        var login = await PostObjectAsync(_anonymous, "/api/identity/login", new
        {
            displayName = "Owner",
            password = "owner-pass-e2e-2026"
        }, cancellationToken);
        Assert.AreEqual("Bearer", login["tokenType"]!.GetValue<string>());

        using var loginClient = clientFactory();
        loginClient.DefaultRequestHeaders.Authorization = Bearer(login["accessToken"]!.GetValue<string>());
        var session = await GetObjectAsync(loginClient, "/api/identity/session", cancellationToken);
        Assert.AreEqual(_familyId, Guid.Parse(session["familyId"]!.GetValue<string>()));

        using var mismatch = await _api.GetAsync($"/api/devices?familyId={Guid.NewGuid()}", cancellationToken);
        Assert.AreEqual(HttpStatusCode.Forbidden, mismatch.StatusCode);

        using var logout = await loginClient.PostAsJsonAsync("/api/identity/logout", new { }, cancellationToken);
        Assert.AreEqual(HttpStatusCode.NoContent, logout.StatusCode);
        using var afterLogout = await loginClient.GetAsync("/api/home/overview", cancellationToken);
        Assert.AreEqual(HttpStatusCode.Unauthorized, afterLogout.StatusCode);
    }

    private async Task VerifyFamilyMembersAndDevicesAsync(CancellationToken cancellationToken)
    {
        var members = await GetArrayAsync(_api, "/api/family/members", cancellationToken);
        Assert.Contains(m => m!["displayName"]!.GetValue<string>() == "Owner", members);

        var permissions = await GetArrayAsync(_api, "/api/family/permissions", cancellationToken);
        Assert.Contains(p => p!["role"]!.GetValue<string>() == "owner", permissions);

        using var invalidRole = await _api.PostAsJsonAsync("/api/family/members", new
        {
            displayName = "Invalid",
            role = "captain"
        }, cancellationToken);
        Assert.AreEqual(HttpStatusCode.BadRequest, invalidRole.StatusCode);

        var member = await PostObjectAsync(_api, "/api/family/members", new
        {
            displayName = "Alex",
            role = "admin",
            password = "alex-pass-e2e-2026"
        }, cancellationToken);
        Assert.AreEqual("admin", member["role"]!.GetValue<string>());

        using var deleted = await _api.DeleteAsync($"/api/family/members/{member["id"]!.GetValue<string>()}", cancellationToken);
        Assert.AreEqual(HttpStatusCode.NoContent, deleted.StatusCode);

        var pairing = await GetObjectAsync(_api, "/api/setup/pairing", cancellationToken);
        Assert.Contains("/pair#code=", pairing["pairingUrl"]!.GetValue<string>());
        using var qr = await _api.GetAsync("/api/setup/pairing.svg", cancellationToken);
        Assert.AreEqual(HttpStatusCode.OK, qr.StatusCode);
        Assert.Contains("image/svg+xml", qr.Content.Headers.ContentType!.MediaType!);
        using var invalidPairing = await _api.PostAsJsonAsync("/api/devices", new
        {
            displayName = "Bad phone",
            kind = "mobile",
            pairingCode = "not-the-code"
        }, cancellationToken);
        Assert.AreEqual(HttpStatusCode.BadRequest, invalidPairing.StatusCode);

        var device = await PostObjectAsync(_api, "/api/devices", new
        {
            familyId = _familyId,
            displayName = "Camera phone",
            kind = "mobile",
            pairingCode = pairing["code"]!.GetValue<string>(),
            issueWebDavToken = true,
            scope = "Photos"
        }, cancellationToken);
        _registeredDeviceId = Guid.Parse(device["device"]!["id"]!.GetValue<string>());
        Assert.AreEqual("mobile", device["device"]!["kind"]!.GetValue<string>());
        Assert.IsFalse(string.IsNullOrWhiteSpace(device["webDav"]!["token"]!.GetValue<string>()));

        var heartbeat = await PostObjectAsync(_api, $"/api/devices/{_registeredDeviceId}/heartbeat", new { }, cancellationToken);
        Assert.IsFalse(string.IsNullOrWhiteSpace(heartbeat["lastSeenAt"]!.GetValue<DateTimeOffset>().ToString("O")));

        var devices = await GetArrayAsync(_api, "/api/devices", cancellationToken);
        Assert.IsGreaterThanOrEqualTo(devices.Count, 2);
    }

    private async Task VerifyWebDavTokensAsync(CancellationToken cancellationToken)
    {
        var token = await PostObjectAsync(_api, "/api/webdav-tokens", new
        {
            familyId = _familyId,
            deviceId = _registeredDeviceId,
            username = "e2e-camera-token",
            scope = "Photos",
            description = "E2E camera roll"
        }, cancellationToken);
        _photosDavUsername = token["username"]!.GetValue<string>();
        _photosDavToken = token["token"]!.GetValue<string>();

        using var duplicate = await _api.PostAsJsonAsync("/api/webdav-tokens", new
        {
            username = _photosDavUsername,
            scope = "Photos"
        }, cancellationToken);
        Assert.AreEqual(HttpStatusCode.Conflict, duplicate.StatusCode);

        var tokens = await GetArrayAsync(_api, "/api/webdav-tokens", cancellationToken);
        Assert.Contains(t => t!["username"]!.GetValue<string>() == _photosDavUsername, tokens);
    }

    private async Task VerifyWebDavAndMediaAsync(CancellationToken cancellationToken)
    {
        using var missingAuth = await _anonymous.GetAsync("/dav/files/", cancellationToken);
        Assert.AreEqual(HttpStatusCode.Unauthorized, missingAuth.StatusCode);
        Assert.Contains("Basic", missingAuth.Headers.WwwAuthenticate.ToString());

        using var dav = CreateDavClient(_initialDavUsername, _initialDavToken);
        using var options = await dav.SendAsync(new HttpRequestMessage(HttpMethod.Options, "/dav/files/"), cancellationToken);
        Assert.AreEqual(HttpStatusCode.NoContent, options.StatusCode);
        var allowHeader = TryReadHeader(options, "Allow");
        if (!string.IsNullOrWhiteSpace(allowHeader))
        {
            Assert.Contains("PROPFIND", allowHeader);
        }

        using var mkcol = await dav.SendAsync(new HttpRequestMessage(new HttpMethod("MKCOL"), "/dav/files/Documents"), cancellationToken);
        Assert.AreEqual((HttpStatusCode)201, mkcol.StatusCode);

        using var put = await dav.PutAsync("/dav/files/Documents/note.txt", new StringContent("hello harbor"), cancellationToken);
        Assert.AreEqual((HttpStatusCode)201, put.StatusCode);
        using var overwrite = await dav.PutAsync("/dav/files/Documents/note.txt", new StringContent("hello harbor again"), cancellationToken);
        Assert.AreEqual(HttpStatusCode.NoContent, overwrite.StatusCode);

        using var propfindRequest = new HttpRequestMessage(new HttpMethod("PROPFIND"), "/dav/files/Documents");
        propfindRequest.Headers.Add("Depth", "1");
        using var propfind = await dav.SendAsync(propfindRequest, cancellationToken);
        Assert.AreEqual(207, (int)propfind.StatusCode);
        Assert.Contains("/dav/files/Documents/note.txt", await propfind.Content.ReadAsStringAsync(cancellationToken));

        Assert.AreEqual("hello harbor again", await dav.GetStringAsync("/dav/files/Documents/note.txt", cancellationToken));
        using var head = await dav.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/dav/files/Documents/note.txt"), cancellationToken);
        Assert.AreEqual(HttpStatusCode.OK, head.StatusCode);

        using var copy = new HttpRequestMessage(new HttpMethod("COPY"), "/dav/files/Documents/note.txt");
        copy.Headers.Add("Destination", new Uri(_api.BaseAddress!, "/dav/files/Documents/copy.txt").ToString());
        using var copied = await dav.SendAsync(copy, cancellationToken);
        Assert.AreEqual((HttpStatusCode)201, copied.StatusCode);

        using var noOverwriteCopy = new HttpRequestMessage(new HttpMethod("COPY"), "/dav/files/Documents/note.txt");
        noOverwriteCopy.Headers.Add("Destination", new Uri(_api.BaseAddress!, "/dav/files/Documents/copy.txt").ToString());
        noOverwriteCopy.Headers.Add("Overwrite", "F");
        using var copyConflict = await dav.SendAsync(noOverwriteCopy, cancellationToken);
        Assert.AreEqual((HttpStatusCode)412, copyConflict.StatusCode);

        using var move = new HttpRequestMessage(new HttpMethod("MOVE"), "/dav/files/Documents/copy.txt");
        move.Headers.Add("Destination", "/dav/files/Documents/moved.txt");
        using var moved = await dav.SendAsync(move, cancellationToken);
        Assert.AreEqual((HttpStatusCode)201, moved.StatusCode);

        using var lockRequest = await dav.SendAsync(new HttpRequestMessage(new HttpMethod("LOCK"), "/dav/files/Documents/moved.txt"), cancellationToken);
        Assert.AreEqual(HttpStatusCode.MethodNotAllowed, lockRequest.StatusCode);

        using var traversal = await dav.PutAsync("/dav/files/%2E%2E/escape.txt", new StringContent("bad"), cancellationToken);
        Assert.IsTrue(traversal.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound);

        using var deleteNote = await dav.DeleteAsync("/dav/files/Documents/note.txt", cancellationToken);
        Assert.AreEqual(HttpStatusCode.NoContent, deleteNote.StatusCode);
        using var deleteMoved = await dav.DeleteAsync("/dav/files/Documents/moved.txt", cancellationToken);
        Assert.AreEqual(HttpStatusCode.NoContent, deleteMoved.StatusCode);

        using var photosDav = CreateDavClient(_photosDavUsername, _photosDavToken);
        using var denied = await photosDav.PutAsync("/dav/files/nope.txt", new StringContent("x"), cancellationToken);
        Assert.AreEqual(HttpStatusCode.Forbidden, denied.StatusCode);
        using var photo = await photosDav.PutAsync("/dav/photos/camera/photo.jpg", new ByteArrayContent([1, 2, 3, 4]), cancellationToken);
        Assert.AreEqual((HttpStatusCode)201, photo.StatusCode);

        var media = await PostObjectAsync(_api, "/api/media/index", new { familyId = _familyId }, cancellationToken);
        Assert.IsGreaterThanOrEqualTo(media["indexed"]!.GetValue<int>(), 1);
        var photos = await GetArrayAsync(_api, "/api/media/assets?type=photo", cancellationToken);
        Assert.IsGreaterThanOrEqualTo(photos.Count, 1);
    }

    private async Task VerifyVaultAndSyncAsync(CancellationToken cancellationToken)
    {
        using var badVault = await _api.PostAsJsonAsync("/api/vault/items", new { name = "Missing" }, cancellationToken);
        Assert.AreEqual(HttpStatusCode.BadRequest, badVault.StatusCode);

        var vault = await PostObjectAsync(_api, "/api/vault/items", new
        {
            name = "Router",
            encryptedPayload = "ciphertext-v1",
            nonce = "nonce-v1",
            keyHint = "family-key-v1"
        }, cancellationToken);
        var vaultId = Guid.Parse(vault["id"]!.GetValue<string>());

        var listed = await GetArrayAsync(_api, "/api/vault/items", cancellationToken);
        var listedVault = listed.Single(v => Guid.Parse(v!["id"]!.GetValue<string>()) == vaultId)!.AsObject();
        Assert.IsFalse(listedVault.ContainsKey("encryptedPayload"));

        var fullVault = await GetObjectAsync(_api, $"/api/vault/items/{vaultId}", cancellationToken);
        Assert.AreEqual("ciphertext-v1", fullVault["encryptedPayload"]!.GetValue<string>());

        var updatedVault = await PostObjectAsync(_api, "/api/vault/items", new
        {
            id = vaultId,
            name = "Router updated",
            encryptedPayload = "ciphertext-v2",
            nonce = "nonce-v2",
            keyHint = "family-key-v2"
        }, cancellationToken);
        Assert.AreEqual("Router updated", updatedVault["name"]!.GetValue<string>());

        var tempVault = await PostObjectAsync(_api, "/api/vault/items", new
        {
            name = "Temporary",
            encryptedPayload = "ciphertext-temp",
            nonce = "nonce-temp",
            keyHint = "family-key-temp"
        }, cancellationToken);
        var tempVaultId = tempVault["id"]!.GetValue<string>();
        using var deleteVault = await _api.DeleteAsync($"/api/vault/items/{tempVaultId}", cancellationToken);
        Assert.AreEqual(HttpStatusCode.NoContent, deleteVault.StatusCode);
        using var missingVault = await _api.GetAsync($"/api/vault/items/{tempVaultId}", cancellationToken);
        Assert.AreEqual(HttpStatusCode.NotFound, missingVault.StatusCode);

        using var badSync = await _api.PostAsJsonAsync("/api/sync/states", new
        {
            deviceId = Guid.Empty,
            scope = "photos"
        }, cancellationToken);
        Assert.AreEqual(HttpStatusCode.BadRequest, badSync.StatusCode);

        var sync = await PostObjectAsync(_api, "/api/sync/states", new
        {
            deviceId = _setupDeviceId,
            scope = "photos",
            cursor = "cursor-1"
        }, cancellationToken);
        Assert.AreEqual("cursor-1", sync["cursor"]!.GetValue<string>());

        var syncUpdated = await PostObjectAsync(_api, "/api/sync/states", new
        {
            deviceId = _setupDeviceId,
            scope = "photos",
            cursor = "cursor-2"
        }, cancellationToken);
        Assert.AreEqual("cursor-2", syncUpdated["cursor"]!.GetValue<string>());

        var states = await GetArrayAsync(_api, "/api/sync/states", cancellationToken);
        Assert.Contains(s => s!["cursor"]!.GetValue<string>() == "cursor-2", states);
    }

    private async Task VerifyAppsNetworkingBackupAndRecoveryAsync(CancellationToken cancellationToken)
    {
        var catalog = await GetArrayAsync(_api, "/api/apps/catalog", cancellationToken);
        Assert.Contains(a => a!["appKey"]!.GetValue<string>() == "jellyfin", catalog);

        using var unknownApp = await _api.PostAsJsonAsync("/api/apps/installs", new { appKey = "unknown" }, cancellationToken);
        Assert.AreEqual(HttpStatusCode.NotFound, unknownApp.StatusCode);

        var app = await PostObjectAsync(_api, "/api/apps/installs", new { appKey = "jellyfin" }, cancellationToken);
        Assert.AreEqual("installed", app["desiredState"]!.GetValue<string>());
        using var appState = await _api.PostAsJsonAsync(
            $"/api/apps/installs/{app["id"]!.GetValue<string>()}/state",
            new { state = "running" },
            cancellationToken);
        Assert.AreEqual(HttpStatusCode.NotImplemented, appState.StatusCode);
        var installs = await GetArrayAsync(_api, "/api/apps/installs", cancellationToken);
        Assert.Contains(i => i!["appKey"]!.GetValue<string>() == "jellyfin", installs);

        using var badCert = await _api.PostAsJsonAsync("/api/networking/certificates/self-signed", new { hostname = "" }, cancellationToken);
        Assert.AreEqual(HttpStatusCode.NotImplemented, badCert.StatusCode);
        using var cert = await _api.PostAsJsonAsync("/api/networking/certificates/self-signed", new
        {
            hostname = "E2E.HomeHarbor.Local",
            days = 30
        }, cancellationToken);
        Assert.AreEqual(HttpStatusCode.NotImplemented, cert.StatusCode);
        Assert.HasCount(0, await GetArrayAsync(_api, "/api/networking/certificates", cancellationToken));

        using var badRoute = await _api.PostAsJsonAsync("/api/networking/proxy/routes", new { hostname = "bad" }, cancellationToken);
        Assert.AreEqual(HttpStatusCode.BadRequest, badRoute.StatusCode);
        var route = await PostObjectAsync(_api, "/api/networking/proxy/routes", new
        {
            hostname = "media.homeharbor.local",
            upstreamUrl = "127.0.0.1:8096",
            tlsEnabled = false
        }, cancellationToken);
        Assert.AreEqual("media.homeharbor.local", route["hostname"]!.GetValue<string>());
        Assert.IsGreaterThanOrEqualTo((await GetArrayAsync(_api, "/api/networking/proxy/routes", cancellationToken)).Count, 1);

        using var badTarget = await _api.PostAsJsonAsync("/api/backups/targets", new { name = "No repository" }, cancellationToken);
        Assert.AreEqual(HttpStatusCode.BadRequest, badTarget.StatusCode);
        var target = await PostObjectAsync(_api, "/api/backups/targets", new
        {
            name = "USB backup",
            repositoryUri = "file:///mnt/homeharbor-backup/e2e"
        }, cancellationToken);
        _backupTargetId = Guid.Parse(target["id"]!.GetValue<string>());

        using var verify = await _api.PostAsJsonAsync($"/api/backups/targets/{_backupTargetId}/verify", new { }, cancellationToken);
        Assert.AreEqual(HttpStatusCode.NotImplemented, verify.StatusCode);
        using var job = await _api.PostAsJsonAsync("/api/backups/run", new { backupTargetId = _backupTargetId }, cancellationToken);
        Assert.AreEqual(HttpStatusCode.NotImplemented, job.StatusCode);
        using var oneClick = await _api.PostAsJsonAsync("/api/backups/one-click", new { backupTargetId = _backupTargetId }, cancellationToken);
        Assert.AreEqual(HttpStatusCode.NotImplemented, oneClick.StatusCode);
        Assert.IsGreaterThanOrEqualTo((await GetArrayAsync(_api, "/api/backups/targets", cancellationToken)).Count, 1);
        Assert.HasCount(0, await GetArrayAsync(_api, "/api/backups/jobs", cancellationToken));

        using var badDrill = await _api.PostAsJsonAsync("/api/recovery/drills", new { backupTargetId = Guid.NewGuid() }, cancellationToken);
        Assert.AreEqual(HttpStatusCode.NotImplemented, badDrill.StatusCode);
        using var localDrill = await _api.PostAsJsonAsync("/api/recovery/drills", new { }, cancellationToken);
        Assert.AreEqual(HttpStatusCode.NotImplemented, localDrill.StatusCode);
        using var targetDrill = await _api.PostAsJsonAsync("/api/recovery/drills", new { backupTargetId = _backupTargetId }, cancellationToken);
        Assert.AreEqual(HttpStatusCode.NotImplemented, targetDrill.StatusCode);
        Assert.HasCount(0, await GetArrayAsync(_api, "/api/recovery/drills", cancellationToken));
    }

    private async Task VerifyRemoteSecurityOtaAndOverviewAsync(CancellationToken cancellationToken)
    {
        var security = await GetObjectAsync(_api, "/api/security/policy", cancellationToken);
        Assert.IsTrue(security["localFirst"]!.GetValue<bool>());

        using var peer1 = await _api.PostAsJsonAsync("/api/remote/wireguard/peers", new
        {
            name = "Travel phone",
            endpoint = "homeharbor.local:51820"
        }, cancellationToken);
        Assert.AreEqual(HttpStatusCode.NotImplemented, peer1.StatusCode);
        using var peer2 = await _api.PostAsJsonAsync("/api/remote/wireguard/peers", new
        {
            name = "Laptop",
            endpoint = "homeharbor.local:51820"
        }, cancellationToken);
        Assert.AreEqual(HttpStatusCode.NotImplemented, peer2.StatusCode);
        Assert.HasCount(0, await GetArrayAsync(_api, "/api/remote/wireguard/peers", cancellationToken));

        var ota = await GetObjectAsync(_api, "/api/ota/status", cancellationToken);
        Assert.IsFalse(string.IsNullOrWhiteSpace(ota["version"]!.GetValue<string>()));

        using var stage = await _api.PostAsJsonAsync("/api/ota/stage", new
        {
            version = "0.2.0-e2e",
            rootfsHash = "sha256:rootfs",
            vbmetaAHash = "sha256:vbmeta-a",
            vbmetaBHash = "sha256:vbmeta-b",
            vbmetaADigest = "sha256:vbmeta-a-digest",
            vbmetaBDigest = "sha256:vbmeta-b-digest",
            createdAt = DateTimeOffset.UtcNow,
            signature = "dev-signature",
            type = "kernel-only"
        }, cancellationToken);
        Assert.AreEqual(HttpStatusCode.NotImplemented, stage.StatusCode);

        using var apply = await _api.PostAsJsonAsync("/api/ota/apply", new
        {
            version = "0.2.0-e2e",
            rootfsHash = "sha256:rootfs",
            vbmetaAHash = "sha256:vbmeta-a",
            vbmetaBHash = "sha256:vbmeta-b",
            vbmetaADigest = "sha256:vbmeta-a-digest",
            vbmetaBDigest = "sha256:vbmeta-b-digest",
            createdAt = DateTimeOffset.UtcNow,
            signature = "dev-signature",
            type = "full-system"
        }, cancellationToken);
        Assert.AreEqual(HttpStatusCode.NotImplemented, apply.StatusCode);

        _ = await GetArrayAsync(_api, "/api/storage/health", cancellationToken);

        var overview = await GetObjectAsync(_api, "/api/home/overview", cancellationToken);
        Assert.IsTrue(overview["initialized"]!.GetValue<bool>());
        Assert.IsFalse(overview["security"]!["endToEndEncryption"]!.GetValue<bool>());
        Assert.IsGreaterThanOrEqualTo(overview["modules"]!["photos"]!["count"]!.GetValue<int>(), 1);
        Assert.IsGreaterThanOrEqualTo(overview["modules"]!["backups"]!["targetCount"]!.GetValue<int>(), 1);
        Assert.IsGreaterThanOrEqualTo(overview["modules"]!["vault"]!["count"]!.GetValue<int>(), 1);
        Assert.IsGreaterThanOrEqualTo(overview["modules"]!["devices"]!["count"]!.GetValue<int>(), 2);
        Assert.IsGreaterThanOrEqualTo(overview["modules"]!["devices"]!["syncStates"]!.GetValue<int>(), 1);
        Assert.AreEqual(0, overview["modules"]!["remoteAccess"]!["peers"]!.GetValue<int>());
        Assert.IsGreaterThanOrEqualTo(overview["modules"]!["runtime"]!["apps"]!.GetValue<int>(), 1);
    }

    private HttpClient CreateDavClient(string username, string token)
    {
        var client = clientFactory();
        client.DefaultRequestHeaders.Authorization = Basic(username, token);
        return client;
    }

    private static async Task<JsonObject> GetObjectAsync(HttpClient client, string path, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(path, cancellationToken);
        return (await ReadJsonAsync(response, HttpStatusCode.OK, cancellationToken)).AsObject();
    }

    private static async Task<JsonArray> GetArrayAsync(HttpClient client, string path, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(path, cancellationToken);
        return (await ReadJsonAsync(response, HttpStatusCode.OK, cancellationToken)).AsArray();
    }

    private static async Task<JsonObject> PostObjectAsync(
        HttpClient client,
        string path,
        object body,
        CancellationToken cancellationToken,
        HttpStatusCode expectedStatus = HttpStatusCode.OK)
    {
        using var response = await client.PostAsJsonAsync(path, body, cancellationToken);
        return (await ReadJsonAsync(response, expectedStatus, cancellationToken)).AsObject();
    }

    private static async Task<JsonNode> ReadJsonAsync(
        HttpResponseMessage response,
        HttpStatusCode expectedStatus,
        CancellationToken cancellationToken)
    {
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        return response.StatusCode != expectedStatus
            ? throw new AssertFailedException(
                $"Expected {(int)expectedStatus} for {response.RequestMessage?.Method} {response.RequestMessage?.RequestUri}, got {(int)response.StatusCode}.\n{text}")
            : JsonNode.Parse(text) ?? throw new AssertFailedException("Expected a JSON response body.");
    }

    private static AuthenticationHeaderValue Bearer(string token) => new("Bearer", token);

    private static AuthenticationHeaderValue Basic(string username, string token)
        => new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{token}")));

    private static string? TryReadHeader(HttpResponseMessage response, string name)
    {
        return response.Headers.TryGetValues(name, out var responseValues)
            ? string.Join(",", responseValues)
            : response.Content.Headers.TryGetValues(name, out var contentValues)
            ? string.Join(",", contentValues)
            : null;
    }
}
