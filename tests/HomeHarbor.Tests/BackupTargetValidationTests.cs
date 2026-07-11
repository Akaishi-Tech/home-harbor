using HomeHarbor.Api.Controllers;

namespace HomeHarbor.Tests;

[TestClass]
public sealed class BackupTargetValidationTests
{
    [TestMethod]
    [DataRow("file:///mnt/homeharbor-backup")]
    [DataRow("file:///mnt/homeharbor-backup/family-a")]
    public void File_Repository_Allows_Backup_Root_And_Descendants(string value)
    {
        Assert.IsTrue(BackupsController.TryNormalizeRepositoryUri(value, out var normalized, out var error), error);
        Assert.AreEqual(value, normalized);
    }

    [TestMethod]
    [DataRow("file://attacker.example/mnt/homeharbor-backup")]
    [DataRow("file://localhost/mnt/homeharbor-backup")]
    [DataRow("file:///mnt/homeharbor-backup/../outside")]
    [DataRow("file:///etc/homeharbor")]
    public void File_Repository_Rejects_Hosts_And_Paths_Outside_Backup_Root(string value)
    {
        Assert.IsFalse(BackupsController.TryNormalizeRepositoryUri(value, out _, out var error));
        Assert.IsFalse(string.IsNullOrWhiteSpace(error));
    }
}
