using HomeHarbor.Api.Data;
using HomeHarbor.Api.Services;
using Microsoft.Extensions.Options;

namespace HomeHarbor.Tests;

[TestClass]
public sealed class ReverseProxyConfigServiceTests
{
    [TestMethod]
    [DataRow("unix///run/podman/podman.sock")]
    [DataRow("http://127.0.0.1:8080/admin")]
    [DataRow("https://example.test?token=secret")]
    [DataRow("http://127.0.0.1:2019")]
    [DataRow("http://192.168.1.10:8080")]
    [DataRow("http://localhost:8080")]
    [DataRow("https://example.test:8443")]
    public void TryNormalizeUserUpstreamUrl_Rejects_Local_Sockets_And_Request_Components(string upstream)
    {
        Assert.IsFalse(ReverseProxyConfigService.TryNormalizeUserUpstreamUrl(upstream, out _, out _));
    }

    [TestMethod]
    public void TryNormalizeHostname_Lowercases_Valid_Dns_Name()
    {
        var valid = ReverseProxyConfigService.TryNormalizeHostname(
            "App.HomeHarbor.Local",
            out var normalized,
            out var error);

        Assert.IsTrue(valid, error);
        Assert.AreEqual("app.homeharbor.local", normalized);
        Assert.AreEqual(string.Empty, error);
    }

    [TestMethod]
    public void TryNormalizeHostname_Rejects_Path_Port_Space_And_Newline()
    {
        foreach (var hostname in new[]
        {
            "",
            "app.homeharbor.local:443",
            "app.homeharbor.local/path",
            "app homeharbor.local",
            "app.homeharbor.local\nrespond 200",
            "-app.homeharbor.local",
            "app-.homeharbor.local",
            "app..homeharbor.local"
        })
        {
            var valid = ReverseProxyConfigService.TryNormalizeHostname(hostname, out var normalized, out var error);

            Assert.IsFalse(valid, hostname);
            Assert.AreEqual(string.Empty, normalized);
            Assert.AreNotEqual(string.Empty, error);
        }
    }

    [TestMethod]
    public void TryNormalizeUpstreamUrl_Allows_Common_Local_Forms()
    {
        foreach (var upstreamUrl in new[]
        {
            "http://localhost:3000",
            "https://192.168.1.10:8443",
            "h2c://[::1]:5000",
            "unix//run/homeharbor-api/api.sock",
            "localhost:3000",
            "127.0.0.1:5181",
            "[::1]:8080",
            "::1"
        })
        {
            var valid = ReverseProxyConfigService.TryNormalizeUpstreamUrl(upstreamUrl, out var normalized, out var error);

            Assert.IsTrue(valid, error);
            Assert.AreEqual(upstreamUrl, normalized);
            Assert.AreEqual(string.Empty, error);
        }
    }

    [TestMethod]
    public void TryNormalizeUpstreamUrl_Rejects_Injection_And_Invalid_Forms()
    {
        foreach (var upstreamUrl in new[]
        {
            "",
            "http://localhost:3000 respond 200",
            "http://localhost:3000\nrespond 200",
            "http://localhost:3000\t",
            "ftp://localhost:21",
            "http:///missing-host",
            "http://localhost:99999",
            "http://local_host:3000",
            "localhost:bad",
            "unix//",
            "http://localhost:3000/{block}"
        })
        {
            var valid = ReverseProxyConfigService.TryNormalizeUpstreamUrl(upstreamUrl, out var normalized, out var error);

            Assert.IsFalse(valid, upstreamUrl);
            Assert.AreEqual(string.Empty, normalized);
            Assert.AreNotEqual(string.Empty, error);
        }
    }

    [TestMethod]
    public void BuildCaddyfile_Normalizes_Hostname_And_Allows_Safe_Upstreams()
    {
        var service = CreateService();

        var caddyfile = service.BuildCaddyfile(
        [
            Route("App.HomeHarbor.Local", "http://127.0.0.1:3000"),
            Route("camera.homeharbor.local", "h2c://127.0.0.1:5000", tlsEnabled: true),
            Route("plain.homeharbor.local", "[::1]:5181")
        ]);

        Assert.Contains("app.homeharbor.local {", caddyfile);
        Assert.Contains("admin unix//run/caddy/admin.sock", caddyfile);
        Assert.Contains("header_up X-Forwarded-For {remote_host}", caddyfile);
        Assert.Contains("header_up X-Forwarded-Proto {scheme}", caddyfile);
        Assert.Contains("reverse_proxy http://127.0.0.1:3000", caddyfile);
        Assert.Contains("reverse_proxy h2c://127.0.0.1:5000", caddyfile);
        Assert.Contains("reverse_proxy [::1]:5181", caddyfile);
        Assert.Contains(":80 {", caddyfile);
        Assert.Contains("@homeHarborCa path /homeharbor-ca.crt", caddyfile);
        Assert.Contains("root * /var/lib/caddy/pki/authorities/local", caddyfile);
        Assert.Contains("rewrite * /root.crt", caddyfile);
        Assert.Contains("Content-Disposition \"attachment; filename=homeharbor-ca.crt\"", caddyfile);
        Assert.Contains("redir https://homeharbor.local{uri} permanent", caddyfile);
        Assert.Contains("homeharbor.local {", caddyfile);
        Assert.Contains("http://app.homeharbor.local {", caddyfile);
        Assert.Contains("redir https://app.homeharbor.local{uri} permanent", caddyfile);
        Assert.IsFalse(caddyfile.Contains("App.HomeHarbor.Local", StringComparison.Ordinal));
    }

    [TestMethod]
    public void BuildCaddyfile_Always_Keeps_Control_Plane_And_Ignores_Reserved_Route()
    {
        var service = CreateService();

        var caddyfile = service.BuildCaddyfile(
        [
            Route("homeharbor.local", "http://127.0.0.1:4444"),
            Route("app.homeharbor.local", "http://127.0.0.1:3000")
        ]);

        Assert.Contains("homeharbor.local {", caddyfile);
        Assert.Contains("reverse_proxy 127.0.0.1:5181", caddyfile);
        Assert.IsFalse(caddyfile.Contains("127.0.0.1:4444", StringComparison.Ordinal));
        Assert.Contains("app.homeharbor.local {", caddyfile);
    }

    [TestMethod]
    public void BuildCaddyfile_Rejects_Stored_Hostname_Line_Injection()
    {
        var service = CreateService();

        var exception = Assert.ThrowsExactly<InvalidOperationException>(() =>
            service.BuildCaddyfile([Route("app.homeharbor.local\nrespond 200", "http://127.0.0.1:3000")]));

        Assert.Contains("Reverse proxy route", exception.Message);
        Assert.Contains("hostname", exception.Message);
    }

    [TestMethod]
    public void BuildCaddyfile_Rejects_Stored_Upstream_Line_Injection()
    {
        var service = CreateService();

        var exception = Assert.ThrowsExactly<InvalidOperationException>(() =>
            service.BuildCaddyfile([Route("app.homeharbor.local", "http://localhost:3000\nrespond 200")]));

        Assert.Contains("Reverse proxy route", exception.Message);
        Assert.Contains("upstreamUrl", exception.Message);
    }

    [TestMethod]
    [DataRow("unix///run/podman/podman.sock")]
    [DataRow("http://127.0.0.1:8080/admin")]
    [DataRow("https://example.test?token=secret")]
    [DataRow("https://example.test#fragment")]
    [DataRow("http://127.0.0.1:2019")]
    [DataRow("http://10.0.0.1:8080")]
    [DataRow("http://localhost:8080")]
    public void BuildCaddyfile_Rejects_Preexisting_Unsafe_User_Routes(string upstream)
    {
        var service = CreateService();

        var exception = Assert.ThrowsExactly<InvalidOperationException>(() =>
            service.BuildCaddyfile([Route("legacy.homeharbor.local", upstream)]));

        Assert.Contains("Reverse proxy route", exception.Message);
    }

    private static ReverseProxyConfigService CreateService()
        => new(Options.Create(new HomeHarborApiOptions
        {
            HttpUpstream = "127.0.0.1:5181"
        }));

    private static ReverseProxyRouteEntity Route(string hostname, string upstreamUrl, bool tlsEnabled = false)
        => new()
        {
            Id = Guid.NewGuid(),
            FamilyId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            Hostname = hostname,
            UpstreamUrl = upstreamUrl,
            TlsEnabled = tlsEnabled,
            CreatedAt = DateTimeOffset.UtcNow
        };
}
