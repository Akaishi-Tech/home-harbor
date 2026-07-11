using HomeHarbor.Api.Services;
using HomeHarbor.Core.Storage;
using Microsoft.Extensions.Options;

namespace HomeHarbor.Tests;

[TestClass]
public sealed class StoragePathPolicyTests
{
    [TestMethod]
    public void NormalizeDavPath_Rejects_Path_Traversal()
    {
        _ = Assert.ThrowsExactly<InvalidOperationException>(() =>
            StoragePathPolicy.NormalizeDavPath("/camera/../escape.jpg"));
    }

    [TestMethod]
    [DataRow("%")]
    [DataRow("%GG")]
    [DataRow("/camera/%")]
    [DataRow("/camera/%2")]
    public void NormalizeDavPath_Rejects_Malformed_Percent_Encoding(string path)
    {
        _ = Assert.ThrowsExactly<InvalidOperationException>(() =>
            StoragePathPolicy.NormalizeDavPath(path));
    }

    [TestMethod]
    [DataRow("/camera/%2e%2e/escape.jpg")]
    [DataRow("/camera%2f..%2fescape.jpg")]
    [DataRow("/camera%5c..%5cescape.jpg")]
    public void NormalizeDavPath_Rejects_Encoded_Path_Traversal(string path)
    {
        _ = Assert.ThrowsExactly<InvalidOperationException>(() =>
            StoragePathPolicy.NormalizeDavPath(path));
    }

    [TestMethod]
    public void NormalizeDavPath_Rejects_Backslash_Path_Traversal()
    {
        _ = Assert.ThrowsExactly<InvalidOperationException>(() =>
            StoragePathPolicy.NormalizeDavPath(@"camera\..\escape.jpg"));
    }

    [TestMethod]
    public void NormalizeDavPath_Normalizes_Backslashes_And_Repeated_Slashes()
    {
        var normalized = StoragePathPolicy.NormalizeDavPath(@"\\camera\\roll///photo.jpg");

        Assert.AreEqual("/camera/roll/photo.jpg", normalized);
    }

    [TestMethod]
    public void NormalizeDavPath_Rejects_Null_Bytes()
    {
        _ = Assert.ThrowsExactly<InvalidOperationException>(() =>
            StoragePathPolicy.NormalizeDavPath("/camera/\0photo.jpg"));
        _ = Assert.ThrowsExactly<InvalidOperationException>(() =>
            StoragePathPolicy.NormalizeDavPath("/camera/%00photo.jpg"));
    }

    [TestMethod]
    public void ResolvePhysicalPath_Stays_Inside_Family_Area()
    {
        var familyId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var path = StoragePathPolicy.ResolvePhysicalPath("/data", familyId, StorageArea.Photos, "/camera/photo.jpg");

        Assert.EndsWith("families/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/photos/camera/photo.jpg", path);
    }

    [TestMethod]
    public void ResolvePhysicalPath_Rejects_Symlink_Inside_Area()
    {
        using var fixture = StorageFixture.Create();
        var outside = Path.Combine(fixture.DataRoot, "outside");
        _ = Directory.CreateDirectory(outside);
        Directory.CreateSymbolicLink(Path.Combine(fixture.FilesRoot, "escape"), outside);

        var exception = Assert.ThrowsExactly<InvalidOperationException>(() =>
            fixture.Storage.Resolve(fixture.FamilyId, StorageArea.Files, "/escape/secret.txt"));

        Assert.Contains("Symbolic links", exception.Message);
    }

    [TestMethod]
    public async Task WriteFileAsync_Rejects_Symlinked_Parent_Without_Writing_Outside()
    {
        using var fixture = StorageFixture.Create();
        var outside = Path.Combine(fixture.DataRoot, "outside");
        _ = Directory.CreateDirectory(outside);
        Directory.CreateSymbolicLink(Path.Combine(fixture.FilesRoot, "escape"), outside);
        await using var input = new MemoryStream("secret"u8.ToArray());

        _ = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => fixture.Storage.WriteFileAsync(
            fixture.FamilyId,
            StorageArea.Files,
            "/escape/written.txt",
            input,
            CancellationToken.None));

        Assert.IsFalse(File.Exists(Path.Combine(outside, "written.txt")));
    }

    [TestMethod]
    public void Delete_Rejects_Symlink_Without_Deleting_Target()
    {
        using var fixture = StorageFixture.Create();
        var outside = Path.Combine(fixture.DataRoot, "outside.txt");
        File.WriteAllText(outside, "keep");
        File.CreateSymbolicLink(Path.Combine(fixture.FilesRoot, "outside.txt"), outside);

        _ = Assert.ThrowsExactly<InvalidOperationException>(() => fixture.Storage.Delete(
            fixture.FamilyId,
            StorageArea.Files,
            "/outside.txt"));

        Assert.AreEqual("keep", File.ReadAllText(outside));
    }

    [TestMethod]
    public void Copy_Rejects_Nested_Symlink_Without_Copying_Target()
    {
        using var fixture = StorageFixture.Create();
        var source = Path.Combine(fixture.FilesRoot, "source");
        _ = Directory.CreateDirectory(source);
        var outside = Path.Combine(fixture.DataRoot, "outside.txt");
        File.WriteAllText(outside, "outside");
        File.CreateSymbolicLink(Path.Combine(source, "linked.txt"), outside);

        _ = Assert.ThrowsExactly<InvalidOperationException>(() => fixture.Storage.Copy(
            fixture.FamilyId,
            StorageArea.Files,
            "/source",
            StorageArea.Files,
            "/copy",
            overwrite: false));

        Assert.IsFalse(File.Exists(Path.Combine(fixture.FilesRoot, "copy", "linked.txt")));
    }

    [TestMethod]
    public void EnumerateFiles_Skips_Symlinks()
    {
        using var fixture = StorageFixture.Create();
        File.WriteAllText(Path.Combine(fixture.FilesRoot, "regular.txt"), "regular");
        var outside = Path.Combine(fixture.DataRoot, "outside.txt");
        File.WriteAllText(outside, "outside");
        File.CreateSymbolicLink(Path.Combine(fixture.FilesRoot, "linked.txt"), outside);

        var files = fixture.Storage.EnumerateFiles(fixture.FamilyId, StorageArea.Files);

        CollectionAssert.AreEqual(new[] { "regular.txt" }, files.Select(file => file.Name).ToArray());
    }

    private sealed class StorageFixture : IDisposable
    {
        private StorageFixture(string dataRoot, Guid familyId, HomeHarborStorageService storage)
        {
            DataRoot = dataRoot;
            FamilyId = familyId;
            Storage = storage;
        }

        public string DataRoot { get; }
        public Guid FamilyId { get; }
        public HomeHarborStorageService Storage { get; }
        public string FilesRoot => Storage.GetAreaRoot(FamilyId, StorageArea.Files);

        public static StorageFixture Create()
        {
            var dataRoot = Path.Combine(Path.GetTempPath(), "HomeHarbor.Tests", Guid.NewGuid().ToString("N"));
            var familyId = Guid.NewGuid();
            var storage = new HomeHarborStorageService(Options.Create(new HomeHarborStorageOptions
            {
                DataRoot = dataRoot,
                MaxUploadBytes = 1024 * 1024
            }));
            storage.EnsureFamilyRoots(familyId);
            return new StorageFixture(dataRoot, familyId, storage);
        }

        public void Dispose()
        {
            if (Directory.Exists(DataRoot)) Directory.Delete(DataRoot, recursive: true);
        }
    }
}
