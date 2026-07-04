using HomeHarbor.Api.Data;
using HomeHarbor.Api.Services;
using HomeHarbor.Core.Storage;
using Microsoft.Extensions.Options;

namespace HomeHarbor.Tests;

[TestClass]
public sealed class ManagedContainerSpecServiceTests
{
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
    public void Normalize_Canonicalizes_Symlinked_HostPath_Inside_Family_Root()
    {
        using var fixture = ContainerSpecFixture.Create();
        var targetRoot = Path.Combine(fixture.FamilyRoot, "media");
        var linkPath = Path.Combine(fixture.FamilyRoot, "media-link");
        _ = Directory.CreateDirectory(targetRoot);
        CreateDirectorySymlinkOrInconclusive(linkPath, targetRoot);

        var definition = fixture.Service.Normalize(
            FamilyId,
            ContainerId,
            RequestWithVolume(Path.Combine(linkPath, "library"), "/data"));

        Assert.AreEqual(Path.Combine(targetRoot, "library"), definition.Volumes[0].HostPath);
        Assert.IsTrue(Directory.Exists(Path.Combine(targetRoot, "library")));
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

    private static ContainerDefinitionRequest RequestWithVolume(string hostPath, string containerPath)
        => new(
            "Test container",
            "docker.io/library/alpine:latest",
            null,
            null,
            [new ContainerVolumeRequest(hostPath, containerPath, false)],
            null,
            null,
            null,
            null,
            null,
            null);

    private static ContainerDefinition DefinitionWithVolume(string hostPath, string containerPath)
        => new(
            "Test container",
            "docker.io/library/alpine:latest",
            new SortedDictionary<string, string>(StringComparer.Ordinal),
            [],
            [new ContainerVolume(hostPath, containerPath, false)],
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
                Image = "docker.io/library/alpine:latest",
                ServiceName = "homeharbor-test-container"
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
