using HomeHarbor.Api.Services;
using HomeHarbor.Core.Identity;

namespace HomeHarbor.Tests;

[TestClass]
public sealed class AppRuntimeCatalogTests
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
    public void List_Keeps_Container_Apps_Recommended_For_Setup()
    {
        var apps = new AppRuntimeCatalog().List(FamilyRoles.Member);

        Assert.Contains(app => app.AppKey == "jellyfin" && app.Kind == "container" && app.RecommendedInSetup, apps);
        Assert.IsTrue(apps.All(app => app.Kind != "system"));
    }

}
