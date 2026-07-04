using HomeHarbor.Api.Services;
using Microsoft.AspNetCore.Http;

namespace HomeHarbor.Tests;

[TestClass]
public sealed class PreStorageRequestGateTests
{
    [TestMethod]
    [DataRow("/")]
    [DataRow("/setup")]
    [DataRow("/assets/index.js")]
    [DataRow("/api/setup")]
    [DataRow("/api/setup/pairing")]
    [DataRow("/api/setup/storage/inventory")]
    [DataRow("/api/system/health")]
    public void RequiresReadyStorage_Allows_PreStorage_Setup_Surface(string path)
    {
        Assert.IsFalse(PreStorageRequestGate.RequiresReadyStorage(new PathString(path)));
    }

    [TestMethod]
    [DataRow("/api/home/overview")]
    [DataRow("/api/identity/login")]
    [DataRow("/api/system")]
    [DataRow("/api/webdav-tokens")]
    [DataRow("/dav/files")]
    public void RequiresReadyStorage_Blocks_Database_Backed_Surface(string path)
    {
        Assert.IsTrue(PreStorageRequestGate.RequiresReadyStorage(new PathString(path)));
    }
}
