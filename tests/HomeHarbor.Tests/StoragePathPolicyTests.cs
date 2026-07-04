using HomeHarbor.Core.Storage;

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
}
