using HomeHarbor.Tooling;

namespace HomeHarbor.Tests;

[TestClass]
public sealed class ReleaseSequenceTests
{
    [TestMethod]
    public void RequireUpgrade_Accepts_Only_Strictly_Newer_Sequence()
    {
        ReleaseSequence.RequireUpgrade(11, 10);

        _ = Assert.ThrowsExactly<InvalidOperationException>(() => ReleaseSequence.RequireUpgrade(10, 10));
        _ = Assert.ThrowsExactly<InvalidOperationException>(() => ReleaseSequence.RequireUpgrade(9, 10));
    }

    [TestMethod]
    public void RequireComponentUpgrade_Allows_A_Release_Pair_To_Converge()
    {
        ReleaseSequence.RequireComponentUpgrade(42, currentComponent: 41, otherComponent: 42);
        ReleaseSequence.RequireComponentUpgrade(43, currentComponent: 42, otherComponent: 42);

        _ = Assert.ThrowsExactly<InvalidOperationException>(
            () => ReleaseSequence.RequireComponentUpgrade(42, currentComponent: 42, otherComponent: 41));
        _ = Assert.ThrowsExactly<InvalidOperationException>(
            () => ReleaseSequence.RequireComponentUpgrade(41, currentComponent: 40, otherComponent: 42));
    }

    [TestMethod]
    public async Task StampRootfsOsReleaseAsync_Writes_One_Positive_Sequence()
    {
        var root = CreateTempDirectory();
        try
        {
            var osRelease = Path.Combine(root, "usr", "lib", "os-release");
            _ = Directory.CreateDirectory(Path.GetDirectoryName(osRelease)!);
            await File.WriteAllTextAsync(
                osRelease,
                "NAME=HomeHarbor\nHOMEHARBOR_RELEASE_SEQUENCE=1\nHOMEHARBOR_RELEASE_SEQUENCE=2\n");

            await ReleaseSequence.StampRootfsOsReleaseAsync(root, 42);

            Assert.AreEqual(42, ReleaseSequence.ReadOsRelease(osRelease));
            _ = Assert.ContainsSingle(
                line => line.StartsWith(ReleaseSequence.OsReleaseKey + "=", StringComparison.Ordinal), File.ReadLines(osRelease));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void TrustedCurrentReleaseFloor_Uses_Maximum_Of_Verified_Root_And_Signed_Kernel()
    {
        var root = CreateTempDirectory();
        try
        {
            var osRelease = Path.Combine(root, "os-release");
            var cmdline = Path.Combine(root, "cmdline");
            File.WriteAllText(osRelease, ReleaseSequence.OsReleaseKey + "=41\n");
            File.WriteAllText(cmdline, "quiet " + ReleaseSequence.KernelArgument + "=42\n");

            var anchors = OtaApplyCommand.TrustedCurrentReleaseAnchors(osRelease, cmdline, allowSequenceBootstrap: false);
            Assert.AreEqual(41, anchors.Root);
            Assert.AreEqual(42, anchors.Kernel);
            Assert.AreEqual(42, anchors.Floor);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void TrustedCurrentReleaseFloor_Fails_Closed_Without_Both_Anchors()
    {
        var root = CreateTempDirectory();
        try
        {
            var osRelease = Path.Combine(root, "os-release");
            var cmdline = Path.Combine(root, "cmdline");
            File.WriteAllText(osRelease, "NAME=HomeHarbor\n");
            File.WriteAllText(cmdline, "quiet\n");

            _ = Assert.ThrowsExactly<InvalidOperationException>(
                () => OtaApplyCommand.TrustedCurrentReleaseAnchors(osRelease, cmdline, allowSequenceBootstrap: false));
            var anchors = OtaApplyCommand.TrustedCurrentReleaseAnchors(osRelease, cmdline, allowSequenceBootstrap: true);
            Assert.AreEqual(0, anchors.Root);
            Assert.AreEqual(0, anchors.Kernel);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void Readers_Reject_Duplicate_Or_NonPositive_Anchors()
    {
        var root = CreateTempDirectory();
        try
        {
            var osRelease = Path.Combine(root, "os-release");
            var cmdline = Path.Combine(root, "cmdline");
            File.WriteAllText(
                osRelease,
                ReleaseSequence.OsReleaseKey + "=1\n" + ReleaseSequence.OsReleaseKey + "=2\n");
            File.WriteAllText(
                cmdline,
                ReleaseSequence.KernelArgument + "=1 " + ReleaseSequence.KernelArgument + "=2\n");

            _ = Assert.ThrowsExactly<InvalidOperationException>(() => ReleaseSequence.ReadOsRelease(osRelease));
            _ = Assert.ThrowsExactly<InvalidOperationException>(() => ReleaseSequence.ReadKernelCommandLine(cmdline));

            File.WriteAllText(osRelease, ReleaseSequence.OsReleaseKey + "=0\n");
            File.WriteAllText(cmdline, ReleaseSequence.KernelArgument + "=-1\n");
            _ = Assert.ThrowsExactly<InvalidOperationException>(() => ReleaseSequence.ReadOsRelease(osRelease));
            _ = Assert.ThrowsExactly<InvalidOperationException>(() => ReleaseSequence.ReadKernelCommandLine(cmdline));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "homeharbor-release-sequence-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(path);
        return path;
    }
}
