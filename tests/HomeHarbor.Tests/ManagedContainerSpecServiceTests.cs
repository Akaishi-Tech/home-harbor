using HomeHarbor.Api.Data;
using HomeHarbor.Api.Services;
using HomeHarbor.Core.Storage;
using Microsoft.Extensions.Options;

namespace HomeHarbor.Tests;

[TestClass]
public sealed class ManagedContainerSpecServiceTests
{
    private const string PinnedImage = "docker.io/library/alpine@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly Guid FamilyId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid ContainerId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    [TestMethod]
    public void Normalize_Rejects_HostPath_Control_Character_Injection()
    {
        using var fixture = ContainerSpecFixture.Create();
        var hostPath = Path.Combine(fixture.FamilyRoot, "media\nPodmanArgs=--privileged");

        var exception = Assert.ThrowsExactly<InvalidOperationException>(() =>
            fixture.Service.Normalize(FamilyId, ContainerId, RequestWithVolume(hostPath, "/data")));

        Assert.Contains("control characters", exception.Message);
    }

    [TestMethod]
    public void Normalize_Rejects_Mutable_Image_Tag()
    {
        using var fixture = ContainerSpecFixture.Create();
        var request = RequestWithVolume(fixture.FamilyRoot, "/data") with
        {
            Image = "docker.io/library/alpine:latest"
        };

        var exception = Assert.ThrowsExactly<InvalidOperationException>(() =>
            fixture.Service.Normalize(FamilyId, ContainerId, request));

        Assert.Contains("untagged repository", exception.Message);
    }

    [TestMethod]
    public void Normalize_Rejects_Tag_Combined_With_Digest_But_Allows_Registry_Port()
    {
        using var fixture = ContainerSpecFixture.Create();
        var tagged = RequestWithVolume(fixture.FamilyRoot, "/data") with
        {
            Image = "registry.example/app:1.0@sha256:" + new string('a', 64)
        };

        var exception = Assert.ThrowsExactly<InvalidOperationException>(() =>
            fixture.Service.Normalize(FamilyId, ContainerId, tagged));
        Assert.Contains("cannot combine a tag", exception.Message);

        var registryWithPort = tagged with
        {
            Image = "registry.example:5000/app@sha256:" + new string('a', 64),
            Volumes = null
        };
        var normalized = fixture.Service.Normalize(FamilyId, ContainerId, registryWithPort);
        Assert.StartsWith("registry.example:5000/app@sha256:", normalized.Image);
    }

    [TestMethod]
    [DataRow("docker.io/library/alpine;X=1@sha256:")]
    [DataRow("docker.io/library/alpine%h@sha256:")]
    [DataRow("docker.io/library/Alpine@sha256:")]
    [DataRow("docker.io//library/alpine@sha256:")]
    [DataRow("registry:70000/alpine@sha256:")]
    public void Normalize_Rejects_Noncanonical_Repository_Syntax(string prefix)
    {
        using var fixture = ContainerSpecFixture.Create();
        var request = RequestWithVolume(fixture.FamilyRoot, "/data") with
        {
            Image = prefix + new string('a', 64)
        };

        _ = Assert.ThrowsExactly<InvalidOperationException>(() =>
            fixture.Service.Normalize(FamilyId, ContainerId, request));
    }

    [TestMethod]
    public void Normalize_Rejects_HostPath_Colon_Option_Injection()
    {
        using var fixture = ContainerSpecFixture.Create();
        var hostPath = Path.Combine(fixture.FamilyRoot, "media:Z");

        var exception = Assert.ThrowsExactly<InvalidOperationException>(() =>
            fixture.Service.Normalize(FamilyId, ContainerId, RequestWithVolume(hostPath, "/data")));

        Assert.Contains("colon", exception.Message);
    }

    [TestMethod]
    public void Normalize_Rejects_ContainerPath_Colon_Option_Injection()
    {
        using var fixture = ContainerSpecFixture.Create();
        var hostPath = Path.Combine(fixture.FamilyRoot, "media");

        var exception = Assert.ThrowsExactly<InvalidOperationException>(() =>
            fixture.Service.Normalize(FamilyId, ContainerId, RequestWithVolume(hostPath, "/data:Z")));

        Assert.Contains("colon", exception.Message);
    }

    [TestMethod]
    public void Normalize_Rejects_Symlinked_HostPath_Escape()
    {
        using var fixture = ContainerSpecFixture.Create();
        var outsideRoot = Path.Combine(fixture.DataRoot, "outside");
        var linkPath = Path.Combine(fixture.FamilyRoot, "escape");
        _ = Directory.CreateDirectory(outsideRoot);
        CreateDirectorySymlinkOrInconclusive(linkPath, outsideRoot);

        var exception = Assert.ThrowsExactly<InvalidOperationException>(() =>
            fixture.Service.Normalize(FamilyId, ContainerId, RequestWithVolume(Path.Combine(linkPath, "etc"), "/data")));

        Assert.Contains("must stay inside", exception.Message);
    }

    [TestMethod]
    public void Normalize_Rejects_Symlinked_HostPath_Inside_Family_Root_Until_Isolation_Exists()
    {
        using var fixture = ContainerSpecFixture.Create();
        var targetRoot = Path.Combine(fixture.FamilyRoot, "media");
        var linkPath = Path.Combine(fixture.FamilyRoot, "media-link");
        _ = Directory.CreateDirectory(targetRoot);
        CreateDirectorySymlinkOrInconclusive(linkPath, targetRoot);

        var exception = Assert.ThrowsExactly<InvalidOperationException>(() => fixture.Service.Normalize(
            FamilyId,
            ContainerId,
            RequestWithVolume(Path.Combine(linkPath, "library"), "/data")));

        Assert.Contains("Family data volumes are unavailable", exception.Message);
    }

    [TestMethod]
    public void Normalize_Rejects_Writable_Family_Data_Volume()
    {
        using var fixture = ContainerSpecFixture.Create();
        var request = RequestWithVolume(fixture.FamilyRoot, "/data") with
        {
            Volumes = [new ContainerVolumeRequest(fixture.FamilyRoot, "/data", ReadOnly: false)]
        };

        var exception = Assert.ThrowsExactly<InvalidOperationException>(() =>
            fixture.Service.Normalize(FamilyId, ContainerId, request));

        Assert.Contains("Family data volumes are unavailable", exception.Message);
    }

    [TestMethod]
    public void Normalize_Rejects_AppRoot_Subdirectory_To_Close_Symlink_Race()
    {
        using var fixture = ContainerSpecFixture.Create();
        var appSubdirectory = Path.Combine(
            fixture.DataRoot,
            "apps",
            ContainerId.ToString("N"),
            "mutable-subdirectory");

        var exception = Assert.ThrowsExactly<InvalidOperationException>(() =>
            fixture.Service.Normalize(
                FamilyId,
                ContainerId,
                RequestWithVolume(appSubdirectory, "/data")));

        Assert.Contains("private app data root", exception.Message);
    }

    [TestMethod]
    public void Normalize_Rejects_ReadOnly_AppRoot_Before_Persisting_Unreconcilable_State()
    {
        using var fixture = ContainerSpecFixture.Create();
        var appRoot = Path.Combine(fixture.DataRoot, "apps", ContainerId.ToString("N"));
        var request = RequestWithVolume(appRoot, "/data");

        var exception = Assert.ThrowsExactly<InvalidOperationException>(() =>
            fixture.Service.Normalize(FamilyId, ContainerId, request));

        Assert.Contains("must be mounted read-write", exception.Message);
    }

    [TestMethod]
    public void BuildQuadlet_Rejects_Stored_Volume_Line_Injection()
    {
        using var fixture = ContainerSpecFixture.Create();
        var definition = DefinitionWithVolume(
            Path.Combine(fixture.FamilyRoot, "media\nPodmanArgs=--privileged"),
            "/data");

        var exception = Assert.ThrowsExactly<InvalidOperationException>(() =>
            fixture.Service.BuildQuadlet(fixture.Container, definition));

        Assert.Contains("control characters", exception.Message);
    }

    [TestMethod]
    public void BuildQuadlet_Rejects_Stored_Symlinked_HostPath_Escape()
    {
        using var fixture = ContainerSpecFixture.Create();
        var outsideRoot = Path.Combine(fixture.DataRoot, "outside");
        var linkPath = Path.Combine(fixture.FamilyRoot, "escape");
        _ = Directory.CreateDirectory(outsideRoot);
        CreateDirectorySymlinkOrInconclusive(linkPath, outsideRoot);
        var definition = DefinitionWithVolume(Path.Combine(linkPath, "etc"), "/data");

        var exception = Assert.ThrowsExactly<InvalidOperationException>(() =>
            fixture.Service.BuildQuadlet(fixture.Container, definition));

        Assert.Contains("must stay inside", exception.Message);
    }

    [TestMethod]
    public void BuildQuadlet_Rejects_Stored_Family_Data_Volume()
    {
        using var fixture = ContainerSpecFixture.Create();
        var definition = DefinitionWithVolume(Path.Combine(fixture.FamilyRoot, "documents"), "/family");

        var exception = Assert.ThrowsExactly<InvalidOperationException>(() =>
            fixture.Service.BuildQuadlet(fixture.Container, definition));

        Assert.Contains("Family data volumes are unavailable", exception.Message);
    }

    [TestMethod]
    public void BuildQuadlet_Uses_Systemd_Argument_Quoting_And_Rejects_Control_Characters()
    {
        using var fixture = ContainerSpecFixture.Create();
        var appRoot = Path.Combine(fixture.DataRoot, "apps", ContainerId.ToString("N"));
        var definition = new ContainerDefinition(
            "Quoted command",
            PinnedImage,
            new SortedDictionary<string, string>(StringComparer.Ordinal),
            [],
            [new ContainerVolume(appRoot, "/data", false)],
            ["run", "single'quote", "double\"quote", "back\\slash", "$HOME", "%n", "two words"]);

        fixture.Container.DesiredState = "running";
        var quadlet = fixture.Service.BuildQuadlet(fixture.Container, definition);

        Assert.Contains(
            "Exec=\"run\" \"single'quote\" \"double\\\"quote\" \"back\\\\slash\" \"$$HOME\" \"%%n\" \"two words\"",
            quadlet);
        Assert.Contains("[Install]\nWantedBy=default.target", quadlet);

        fixture.Container.DesiredState = "stopped";
        var stoppedQuadlet = fixture.Service.BuildQuadlet(fixture.Container, definition);
        Assert.IsFalse(stoppedQuadlet.Contains("[Install]", StringComparison.Ordinal));

        var invalid = definition with { Command = ["run", "bad\0argument"] };
        var exception = Assert.ThrowsExactly<InvalidOperationException>(() =>
            fixture.Service.BuildQuadlet(fixture.Container, invalid));
        Assert.Contains("control characters", exception.Message);
    }

    [TestMethod]
    public void BuildQuadlet_Binds_Published_Ports_Only_To_Loopback()
    {
        using var fixture = ContainerSpecFixture.Create();
        var appRoot = Path.Combine(fixture.DataRoot, "apps", ContainerId.ToString("N"));
        var definition = new ContainerDefinition(
            "Loopback ports",
            PinnedImage,
            new SortedDictionary<string, string>(StringComparer.Ordinal),
            [new ContainerPort(8443, 443, "tcp"), new ContainerPort(5353, 53, "udp")],
            [new ContainerVolume(appRoot, "/data", false)],
            []);

        var quadlet = fixture.Service.BuildQuadlet(fixture.Container, definition);

        Assert.Contains("PublishPort=127.0.0.1:8443:443\n", quadlet);
        Assert.Contains("PublishPort=127.0.0.1:5353:53/udp\n", quadlet);
        Assert.DoesNotContain("PublishPort=8443:443", quadlet);
        Assert.DoesNotContain("PublishPort=5353:53/udp", quadlet);
    }

    [TestMethod]
    public void BuildQuadlet_Uses_Unique_User_Namespace_And_Private_Ownership_Mount()
    {
        using var fixture = ContainerSpecFixture.Create();
        var appRoot = Path.Combine(fixture.DataRoot, "apps", ContainerId.ToString("N"));
        var definition = DefinitionWithVolume(appRoot, "/data");

        var quadlet = fixture.Service.BuildQuadlet(fixture.Container, definition);

        Assert.Contains("UserNS=auto\n", quadlet);
        Assert.Contains($"Volume=\"{appRoot}:/data:rw,U\"\n", quadlet);
        Assert.DoesNotContain("keep-groups", quadlet);
    }

    [TestMethod]
    public void Normalize_Leaves_App_Root_Creation_To_The_Privileged_Agent()
    {
        using var fixture = ContainerSpecFixture.Create();
        var appRoot = Path.Combine(fixture.DataRoot, "apps", ContainerId.ToString("N"));

        var definition = fixture.Service.Normalize(
            FamilyId,
            ContainerId,
            RequestWithVolume(appRoot, "/data") with { Volumes = null });

        Assert.IsFalse(Directory.Exists(appRoot));
        Assert.AreEqual(appRoot, definition.Volumes.Single().HostPath);
    }

    [TestMethod]
    public void EnsurePortsAvailable_Rejects_Global_Host_Port_Conflict()
    {
        using var fixture = ContainerSpecFixture.Create();
        var existingDefinition = DefinitionWithPort(8443, "tcp");
        var existing = new ManagedContainerEntity
        {
            Id = Guid.NewGuid(),
            FamilyId = Guid.NewGuid(),
            Name = "Other family container",
            DefinitionJson = fixture.Service.Serialize(existingDefinition)
        };

        var exception = Assert.ThrowsExactly<InvalidOperationException>(() =>
            fixture.Service.EnsurePortsAvailable(DefinitionWithPort(8443, "tcp"), [existing]));

        Assert.Contains("8443/tcp", exception.Message);
    }

    [TestMethod]
    public void EnsurePortsAvailable_Allows_Different_Protocol_And_Excluded_Self()
    {
        using var fixture = ContainerSpecFixture.Create();
        var existing = new ManagedContainerEntity
        {
            Id = ContainerId,
            FamilyId = FamilyId,
            Name = "Current container",
            DefinitionJson = fixture.Service.Serialize(DefinitionWithPort(5353, "tcp"))
        };

        fixture.Service.EnsurePortsAvailable(DefinitionWithPort(5353, "udp"), [existing]);
        fixture.Service.EnsurePortsAvailable(DefinitionWithPort(5353, "tcp"), [existing], ContainerId);
    }

    private static ContainerDefinitionRequest RequestWithVolume(string hostPath, string containerPath)
        => new(
            "Test container",
            PinnedImage,
            null,
            null,
            [new ContainerVolumeRequest(hostPath, containerPath, true)],
            null,
            null,
            null,
            null,
            null,
            null);

    private static ContainerDefinition DefinitionWithVolume(string hostPath, string containerPath)
        => new(
            "Test container",
            PinnedImage,
            new SortedDictionary<string, string>(StringComparer.Ordinal),
            [],
            [new ContainerVolume(hostPath, containerPath, false)],
            []);

    private static ContainerDefinition DefinitionWithPort(int hostPort, string protocol)
        => new(
            "Test container",
            PinnedImage,
            new SortedDictionary<string, string>(StringComparer.Ordinal),
            [new ContainerPort(hostPort, 443, protocol)],
            [],
            []);

    private static void CreateDirectorySymlinkOrInconclusive(string linkPath, string targetPath)
    {
        string? inconclusiveReason = null;
        try
        {
            _ = Directory.CreateSymbolicLink(linkPath, targetPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            inconclusiveReason = $"Directory symlinks are not available in this test environment: {exception.Message}";
        }

        if (inconclusiveReason is not null)
        {
            Assert.Inconclusive(inconclusiveReason);
        }
    }

    private sealed class ContainerSpecFixture : IDisposable
    {
        private ContainerSpecFixture(string dataRoot, ManagedContainerSpecService service)
        {
            DataRoot = dataRoot;
            Service = service;
            FamilyRoot = Path.Combine(dataRoot, "families", FamilyId.ToString("N"));
            Container = new ManagedContainerEntity
            {
                Id = ContainerId,
                FamilyId = FamilyId,
                Name = "Test container",
                Image = PinnedImage,
                ServiceName = $"homeharbor-{ContainerId:N}"
            };
        }

        public string DataRoot { get; }
        public string FamilyRoot { get; }
        public ManagedContainerSpecService Service { get; }
        public ManagedContainerEntity Container { get; }

        public static ContainerSpecFixture Create()
        {
            var dataRoot = Path.Combine(Path.GetTempPath(), "HomeHarbor.Tests", Guid.NewGuid().ToString("N"));
            var storage = new HomeHarborStorageService(Options.Create(new HomeHarborStorageOptions
            {
                DataRoot = dataRoot,
                MaxUploadBytes = 1024 * 1024
            }));
            storage.EnsureFamilyRoots(FamilyId);
            return new ContainerSpecFixture(dataRoot, new ManagedContainerSpecService(storage));
        }

        public void Dispose()
        {
            if (Directory.Exists(DataRoot)) Directory.Delete(DataRoot, recursive: true);
        }
    }
}
