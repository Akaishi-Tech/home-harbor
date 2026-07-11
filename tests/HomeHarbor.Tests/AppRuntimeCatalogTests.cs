using System.Text.Json;
using System.Text.Json.Nodes;
using HomeHarbor.Api.Services;
using HomeHarbor.Core.Identity;
using HomeHarbor.Tooling;

namespace HomeHarbor.Tests;

[TestClass]
public sealed partial class AppRuntimeCatalogTests
{
    [TestMethod]
    public void List_Does_Not_Include_Zfs_Utils_System_App()
    {
        var catalog = new AppRuntimeCatalog();

        Assert.DoesNotContain(app => app.AppKey == "zfs-utils", catalog.List(FamilyRoles.Member));
        Assert.DoesNotContain(app => app.AppKey == "zfs-utils", catalog.List(FamilyRoles.Guest));
        Assert.DoesNotContain(app => app.AppKey == "zfs-utils", catalog.List(FamilyRoles.Owner));
        Assert.DoesNotContain(app => app.AppKey == "zfs-utils", catalog.List(FamilyRoles.Admin));
    }

    [TestMethod]
    public void List_Does_Not_Recommend_Unprovisioned_Container_Apps_For_Setup()
    {
        var apps = new AppRuntimeCatalog().List(FamilyRoles.Member);

        Assert.IsTrue(apps.All(app => !app.RecommendedInSetup));
        Assert.IsTrue(apps.All(app => app.Kind != "system"));
    }

    [TestMethod]
    public void Built_In_Catalog_Only_Advertises_Runnable_Templates_As_Available()
    {
        var apps = new AppRuntimeCatalog().List(FamilyRoles.Owner);
        var vaultwarden = apps.Single(app => app.AppKey == "vaultwarden");

        Assert.IsFalse(vaultwarden.Available);
        Assert.Contains("TLS ingress", vaultwarden.UnavailableReason);
        var install = (HomeHarborContainerAppInstall)vaultwarden.Manifest.Install;
        Assert.Contains(
            port => port.HostPort == 8081 && port.ContainerPort == 80 && port.Protocol == "tcp",
            install.Ports);

        foreach (var appKey in new[] { "vaultwarden", "jellyfin", "syncthing", "immich" })
        {
            var unavailable = apps.Single(app => app.AppKey == appKey);
            Assert.IsFalse(unavailable.Available);
            Assert.IsFalse(unavailable.RecommendedInSetup);
            Assert.IsFalse(string.IsNullOrWhiteSpace(unavailable.UnavailableReason));
        }
    }

    [TestMethod]
    public void Built_In_Container_Images_Are_Pinned_To_Sha256_Digests()
    {
        var apps = new AppRuntimeCatalog().List(FamilyRoles.Owner);

        Assert.IsNotEmpty(apps);
        Assert.IsTrue(apps
            .Where(app => app.Kind == "container")
            .All(app =>
            {
                if (!MyRegex().IsMatch(app.Image))
                {
                    return false;
                }

                var repository = app.Image[..app.Image.LastIndexOf("@sha256:", StringComparison.Ordinal)];
                return repository.LastIndexOf(':') <= repository.LastIndexOf('/');
            }));
    }

    [TestMethod]
    public void App_Manifest_Rejects_Mutable_Container_Image_Tag()
    {
        using var document = JsonDocument.Parse("""
            {
              "schemaVersion": 1,
              "kind": "homeharbor.app",
              "appKey": "mutable-app",
              "version": "1.0.0",
              "channel": "dev",
              "displayName": "Mutable app",
              "title": "Mutable app",
              "description": "Mutable image test",
              "category": "test",
              "recommendedInSetup": false,
              "visibleRoles": [],
              "install": {
                "type": "container",
                "image": "docker.io/example/app:latest",
                "ports": [],
                "environment": {},
                "volumes": [],
                "command": []
              }
            }
            """);

        var exception = Assert.Throws<InvalidOperationException>(
            () => HomeHarborAppManifestVerifier.ParseTrustedAppManifest(document.RootElement));

        Assert.Contains("untagged repository reference", exception.Message);
    }

    [TestMethod]
    public void App_Manifest_Rejects_Tag_Combined_With_Digest()
    {
        using var document = ContainerManifest(install =>
            install["image"] = "registry.example/app:1.0@sha256:" + new string('a', 64));

        var exception = Assert.ThrowsExactly<InvalidOperationException>(() =>
            HomeHarborAppManifestVerifier.ParseTrustedAppManifest(document.RootElement));

        Assert.Contains("cannot combine a tag", exception.Message);
    }

    [TestMethod]
    public void App_Manifest_Rejects_Unsafe_Port_Environment_And_Command_Fields()
    {
        using (var document = ContainerManifest(install =>
               install["ports"] = JsonNode.Parse("[{\"hostPort\":80,\"containerPort\":80,\"protocol\":\"tcp\"}]")!))
        {
            var exception = Assert.ThrowsExactly<InvalidOperationException>(() =>
                HomeHarborAppManifestVerifier.ParseTrustedAppManifest(document.RootElement));
            Assert.Contains("1024", exception.Message);
        }

        using (var document = ContainerManifest(install =>
               install["environment"] = JsonNode.Parse("{\"UNSAFE\":123}")!))
        {
            var exception = Assert.ThrowsExactly<InvalidOperationException>(() =>
                HomeHarborAppManifestVerifier.ParseTrustedAppManifest(document.RootElement));
            Assert.Contains("must be a string", exception.Message);
        }

        using (var document = ContainerManifest(install =>
               install["command"] = new JsonArray("run", "bad\0argument")))
        {
            var exception = Assert.ThrowsExactly<InvalidOperationException>(() =>
                HomeHarborAppManifestVerifier.ParseTrustedAppManifest(document.RootElement));
            Assert.Contains("command argument", exception.Message);
        }
    }

    [TestMethod]
    public void App_Manifest_Rejects_Cleartext_System_App_Manifest_Url()
    {
        using var document = ContainerManifest(install =>
        {
            install.Clear();
            install["type"] = "system";
            install["mode"] = "usr-overlay";
            install["manifestUrl"] = "http://example.com/system-app.json";
            install["commands"] = new JsonArray("example");
        });

        var exception = Assert.ThrowsExactly<InvalidOperationException>(() =>
            HomeHarborAppManifestVerifier.ParseTrustedAppManifest(document.RootElement));

        Assert.Contains("HTTPS", exception.Message);
    }

    [TestMethod]
    public void System_App_Templates_Are_Unavailable_Until_Persistent_Activation_Exists()
    {
        using var document = ContainerManifest(install =>
        {
            install.Clear();
            install["type"] = "system";
            install["mode"] = "usr-overlay";
            install["manifestUrl"] = "https://updates.example.com/system-app.json";
            install["commands"] = new JsonArray("example");
        });
        var manifest = HomeHarborAppManifestVerifier.ParseTrustedAppManifest(document.RootElement);

        var template = ManagedAppTemplate.FromManifest(manifest, source: "remote", signedManifestJson: "{}");

        Assert.AreEqual("system", template.Kind);
        Assert.IsFalse(template.Available);
        Assert.IsFalse(template.RecommendedInSetup);
        Assert.Contains("Persistent system-app activation", template.UnavailableReason);
    }

    [TestMethod]
    public void System_App_Template_Is_Unavailable_Until_Persistent_Activation_Exists()
    {
        using var document = ContainerManifest(install =>
        {
            install.Clear();
            install["type"] = "system";
            install["mode"] = "usr-overlay";
            install["manifestUrl"] = "https://updates.example.com/system-app.json";
            install["commands"] = new JsonArray("example");
        });
        var manifest = HomeHarborAppManifestVerifier.ParseTrustedAppManifest(document.RootElement);

        var template = ManagedAppTemplate.FromManifest(manifest, signedManifestJson: "{\"signed\":true}");

        Assert.IsFalse(template.Available);
        Assert.IsFalse(template.RecommendedInSetup);
        Assert.Contains("Persistent system-app activation", template.UnavailableReason);
    }

    [TestMethod]
    public void Store_Index_Rejects_Duplicate_App_Keys()
    {
        using var document = JsonDocument.Parse("""
            {
              "schemaVersion": 1,
              "kind": "homeharbor.app-store",
              "channel": "dev",
              "generatedAt": "2026-07-11T00:00:00Z",
              "apps": [
                {
                  "appKey": "duplicate",
                  "version": "1.0.0",
                  "manifestUrl": "https://example.com/one.json",
                  "manifestSha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
                },
                {
                  "appKey": "duplicate",
                  "version": "2.0.0",
                  "manifestUrl": "https://example.com/two.json",
                  "manifestSha256": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
                }
              ]
            }
            """);

        var exception = Assert.ThrowsExactly<InvalidOperationException>(() =>
            HomeHarborAppManifestVerifier.ParseTrustedStoreIndex(document.RootElement));

        Assert.Contains("duplicate app keys", exception.Message);
    }

    private static JsonDocument ContainerManifest(Action<JsonObject> mutateInstall)
    {
        var root = JsonNode.Parse("""
            {
              "schemaVersion": 1,
              "kind": "homeharbor.app",
              "appKey": "safe-app",
              "version": "1.0.0",
              "channel": "dev",
              "displayName": "Safe app",
              "title": "Safe app",
              "description": "Manifest validation test",
              "category": "test",
              "recommendedInSetup": false,
              "visibleRoles": [],
              "install": {
                "type": "container",
                "image": "docker.io/example/app@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                "ports": [],
                "environment": {},
                "volumes": [],
                "command": []
              }
            }
            """)!.AsObject();
        mutateInstall(root["install"]!.AsObject());
        return JsonDocument.Parse(root.ToJsonString());
    }

    [System.Text.RegularExpressions.GeneratedRegex("@sha256:[0-9a-f]{64}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex MyRegex();
}
