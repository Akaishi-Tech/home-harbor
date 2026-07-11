using HomeHarbor.Tooling;

namespace HomeHarbor.Tests;

[TestClass]
public sealed class BlockDeviceSafetyTests
{
    [TestMethod]
    public async Task RootAncestorDevices_Allows_Verified_Archiso_Overlay_And_Protects_Its_Media()
    {
        var runner = ArchisoRunner("/run/archiso/bootmnt/hh/x86_64/airootfs.erofs");

        var devices = await BlockDeviceSafety.RootAncestorDevicesAsync(
            runner,
            allowVerifiedArchisoRoot: true,
            CancellationToken.None);

        CollectionAssert.AreEquivalent(new[] { "/dev/sr0", "/dev/loop0" }, devices.ToArray());
    }

    [TestMethod]
    public async Task RootAncestorDevices_Rejects_Archiso_Overlay_Without_Installer_Context()
    {
        var runner = ArchisoRunner("/run/archiso/bootmnt/hh/x86_64/airootfs.erofs");

        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            BlockDeviceSafety.RootAncestorDevicesAsync(runner, CancellationToken.None));

        Assert.Contains("invalid root filesystem device", error.Message);
    }

    [TestMethod]
    public async Task RootAncestorDevices_Rejects_Archiso_Loop_Backed_Outside_Boot_Media()
    {
        var runner = ArchisoRunner("/homeharbor-data/attacker-controlled.erofs");

        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            BlockDeviceSafety.RootAncestorDevicesAsync(
                runner,
                allowVerifiedArchisoRoot: true,
                CancellationToken.None));

        Assert.Contains("not backed by the verified boot media", error.Message);
    }

    [TestMethod]
    public async Task RootAncestorDevices_Preserves_Normal_Block_Root_Protection()
    {
        var runner = new RecordingRunner((fileName, args) => (fileName, args) switch
        {
            ("findmnt", ["-n", "-o", "SOURCE", "/"]) => Success(fileName, "/dev/vda2\n"),
            ("readlink", ["-f", "/dev/vda2"]) => Success(fileName, "/dev/vda2\n"),
            ("readlink", ["-f", "/dev/vda"]) => Success(fileName, "/dev/vda\n"),
            ("lsblk", ["-nrpo", "PATH", "-s", "/dev/vda2"]) => Success(fileName, "/dev/vda2\n/dev/vda\n"),
            _ => Failure(fileName)
        });

        var devices = await BlockDeviceSafety.RootAncestorDevicesAsync(runner, CancellationToken.None);

        CollectionAssert.AreEquivalent(new[] { "/dev/vda2", "/dev/vda" }, devices.ToArray());
    }

    [TestMethod]
    public async Task DeviceHasMounts_Requests_A_Tree_And_Finds_Child_Mounts()
    {
        var runner = new RecordingRunner((fileName, args) => (fileName, args) switch
        {
            ("lsblk", ["--json", "--tree", "--output", "PATH,MOUNTPOINTS", "/dev/vdb"]) =>
                Success(fileName, """
                    {"blockdevices":[{"path":"/dev/vdb","mountpoints":[],"children":[
                      {"path":"/dev/vdb1","mountpoints":["/mnt/data"]}
                    ]}]}
                    """),
            _ => Failure(fileName)
        });

        Assert.IsTrue(await BlockDeviceSafety.DeviceHasMountsAsync(
            runner,
            "/dev/vdb",
            CancellationToken.None));
    }

    [TestMethod]
    public async Task DeviceLabels_Reads_Nested_Child_Labels()
    {
        var runner = new RecordingRunner((fileName, args) => (fileName, args) switch
        {
            ("lsblk", ["--json", "--tree", "--output", "PATH,LABEL,PARTLABEL", "/dev/vdb"]) =>
                Success(fileName, """
                    {"blockdevices":[{"path":"/dev/vdb","label":null,"partlabel":null,"children":[
                      {"path":"/dev/vdb1","label":"DATA","partlabel":"homeharbor-data"}
                    ]}]}
                    """),
            _ => Failure(fileName)
        });

        var labels = await BlockDeviceSafety.DeviceLabelsAsync(
            runner,
            "/dev/vdb",
            CancellationToken.None);

        CollectionAssert.AreEquivalent(new[] { "DATA", "homeharbor-data" }, labels.ToArray());
    }

    [TestMethod]
    public async Task DeviceHasMounts_Rejects_Flat_MultiRoot_Response()
    {
        var runner = new RecordingRunner((fileName, args) => (fileName, args) switch
        {
            ("lsblk", ["--json", "--tree", "--output", "PATH,MOUNTPOINTS", "/dev/vdb"]) =>
                Success(fileName, """
                    {"blockdevices":[
                      {"path":"/dev/vdb","mountpoints":[]},
                      {"path":"/dev/vdb1","mountpoints":[]}
                    ]}
                    """),
            _ => Failure(fileName)
        });

        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            BlockDeviceSafety.DeviceHasMountsAsync(runner, "/dev/vdb", CancellationToken.None));

        Assert.Contains("did not return exactly one device", error.Message);
    }

    private static RecordingRunner ArchisoRunner(string backingFile)
        => new((fileName, args) => (fileName, args) switch
        {
            ("findmnt", ["-n", "-o", "SOURCE", "/"]) => Success(fileName, "airootfs\n"),
            ("findmnt", ["-n", "-o", "FSTYPE", "/"]) => Success(fileName, "overlay\n"),
            ("findmnt", ["-n", "-o", "SOURCE,FSTYPE", "/run/archiso/bootmnt"]) =>
                Success(fileName, "/dev/sr0 iso9660\n"),
            ("findmnt", ["-n", "-o", "SOURCE,FSTYPE", "/run/archiso/airootfs"]) =>
                Success(fileName, "/dev/loop0 erofs\n"),
            ("readlink", ["-f", "/dev/loop0"]) => Success(fileName, "/dev/loop0\n"),
            ("readlink", ["-f", "/dev/sr0"]) => Success(fileName, "/dev/sr0\n"),
            ("losetup", ["--noheadings", "--output", "BACK-FILE", "/dev/loop0"]) =>
                Success(fileName, backingFile + "\n"),
            ("lsblk", ["-nrpo", "PATH", "-s", "/dev/sr0"]) => Success(fileName, "/dev/sr0\n"),
            ("lsblk", ["-nrpo", "PATH", "-s", "/dev/loop0"]) => Success(fileName, "/dev/loop0\n"),
            _ => Failure(fileName)
        });

    private static CommandResult Success(string command, string stdout)
        => new(0, stdout, string.Empty, command);

    private static CommandResult Failure(string command)
        => new(1, string.Empty, "unexpected command", command);

    private sealed class RecordingRunner(Func<string, string[], CommandResult> handler) : ICommandRunner
    {
        public Task<CommandResult> RunAsync(
            string fileName,
            IEnumerable<string> arguments,
            CommandRunOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(handler(fileName, arguments.ToArray()));
    }
}
