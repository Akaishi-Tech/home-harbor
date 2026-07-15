using HomeHarbor.Tooling;

namespace HomeHarbor.Tests;

[TestClass]
public sealed class OtaDataUnlockCompatibilityTests
{
    [TestMethod]
    [DataRow("", "passphrase")]
    [DataRow("# Unlock configuration is persisted outside the immutable rootfs.\n", "tpm2")]
    public void Empty_Target_Crypttab_Inherits_Persistent_Current_Mode(string crypttab, string currentMode)
    {
        var path = WriteCrypttab(crypttab);
        try
        {
            Assert.AreEqual(currentMode, OtaApplyCommand.ResolveTargetDataUnlockMode(path, currentMode));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [TestMethod]
    [DataRow("homeharbor-data UUID=test none\n", "passphrase")]
    [DataRow("homeharbor-data UUID=test none tpm2-device=auto\n", "tpm2")]
    public void Explicit_Target_Crypttab_Mode_Is_Still_Validated(string crypttab, string expectedMode)
    {
        var path = WriteCrypttab(crypttab);
        try
        {
            Assert.AreEqual(expectedMode, OtaApplyCommand.ResolveTargetDataUnlockMode(path, "passphrase"));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [TestMethod]
    [DataRow("other-data UUID=test none\n")]
    [DataRow("homeharbor-data UUID=test none\nhomeharbor-data UUID=other none tpm2-device=auto\n")]
    public void Target_Crypttab_With_Ambiguous_Or_Unrelated_Entries_Fails_Closed(string crypttab)
    {
        var path = WriteCrypttab(crypttab);
        try
        {
            var error = Assert.ThrowsExactly<InvalidOperationException>(() =>
                OtaApplyCommand.ResolveTargetDataUnlockMode(path, "passphrase"));

            Assert.Contains("exactly one valid homeharbor-data entry", error.Message);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [TestMethod]
    public void Missing_Target_Crypttab_Fails_Closed()
    {
        var path = Path.Combine(Path.GetTempPath(), "homeharbor-missing-crypttab-" + Guid.NewGuid().ToString("N"));

        var error = Assert.ThrowsExactly<InvalidOperationException>(() =>
            OtaApplyCommand.ResolveTargetDataUnlockMode(path, "passphrase"));

        Assert.Contains("target rootfs crypttab is missing", error.Message);
    }

    [TestMethod]
    public void Unreadable_Target_Crypttab_Fails_Closed()
    {
        var path = WriteCrypttab(string.Empty);
        try
        {
            File.SetUnixFileMode(path, UnixFileMode.None);

            var error = Assert.ThrowsExactly<InvalidOperationException>(() =>
                OtaApplyCommand.ResolveTargetDataUnlockMode(path, "passphrase"));

            Assert.Contains("could not read target rootfs crypttab", error.Message);
        }
        finally
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [TestMethod]
    public void Empty_Target_Crypttab_Does_Not_Mask_Invalid_Persistent_Mode()
    {
        var path = WriteCrypttab(string.Empty);
        try
        {
            var error = Assert.ThrowsExactly<InvalidOperationException>(() =>
                OtaApplyCommand.ResolveTargetDataUnlockMode(path, "unsupported-metadata"));

            Assert.Contains("current data unlock mode is unsupported", error.Message);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    private static string WriteCrypttab(string contents)
    {
        var directory = Directory.CreateTempSubdirectory("homeharbor-ota-crypttab-");
        var path = Path.Combine(directory.FullName, "crypttab");
        File.WriteAllText(path, contents);
        return path;
    }
}
