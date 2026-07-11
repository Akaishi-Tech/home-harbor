using HomeHarbor.Api.Controllers;

namespace HomeHarbor.Tests;

[TestClass]
public sealed class SyncControllerTests
{
    [TestMethod]
    public void Device_LastSeen_Is_Refreshed_Initially_And_After_Five_Minutes()
    {
        var now = new DateTimeOffset(2026, 7, 11, 10, 0, 0, TimeSpan.Zero);

        Assert.IsTrue(SyncController.ShouldRefreshDeviceLastSeen(null, now));
        Assert.IsFalse(SyncController.ShouldRefreshDeviceLastSeen(now.AddMinutes(-4).AddSeconds(-59), now));
        Assert.IsTrue(SyncController.ShouldRefreshDeviceLastSeen(now.AddMinutes(-5), now));
        Assert.IsTrue(SyncController.ShouldRefreshDeviceLastSeen(now.AddHours(-1), now));
    }

    [TestMethod]
    public void Device_LastSeen_Does_Not_Move_Backwards_When_Clock_Is_Ahead()
    {
        var now = new DateTimeOffset(2026, 7, 11, 10, 0, 0, TimeSpan.Zero);

        Assert.IsFalse(SyncController.ShouldRefreshDeviceLastSeen(now.AddMinutes(1), now));
    }
}
