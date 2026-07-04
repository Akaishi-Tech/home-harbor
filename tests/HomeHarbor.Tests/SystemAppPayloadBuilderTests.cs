using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.Versioning;
using System.Text;
using HomeHarbor.Tooling;

namespace HomeHarbor.Tests;

[TestClass]
public sealed class SystemAppPayloadBuilderTests
{
    [TestMethod]
    public void ToPayloadRelativePath_Allows_Only_Usr_Payloads()
    {
        Assert.AreEqual("usr/bin/zfs", SystemAppPayloadBuilder.ToPayloadRelativePath("/usr/bin/zfs"));
        Assert.AreEqual("usr/lib/libzfs.so", SystemAppPayloadBuilder.ToPayloadRelativePath("/usr/lib/libzfs.so/"));

        _ = Assert.ThrowsExactly<InvalidOperationException>(() => SystemAppPayloadBuilder.ToPayloadRelativePath("/etc/zfs/zpool.cache"));
        _ = Assert.ThrowsExactly<InvalidOperationException>(() => SystemAppPayloadBuilder.ToPayloadRelativePath("/var/lib/zfs"));
        _ = Assert.ThrowsExactly<InvalidOperationException>(() => SystemAppPayloadBuilder.ToPayloadRelativePath("usr/bin/zfs"));
        _ = Assert.ThrowsExactly<InvalidOperationException>(() => SystemAppPayloadBuilder.ToPayloadRelativePath("/usr/../etc/passwd"));
    }

    [TestMethod]
    public void ValidateSymlinkTarget_Rejects_Targets_Outside_Usr()
    {
        SystemAppPayloadBuilder.ValidateSymlinkTarget("usr/bin/zfs", "../lib/libzfs.so");
        SystemAppPayloadBuilder.ValidateSymlinkTarget("usr/lib/libzfs.so", "/usr/lib/libzfs.so.4");

        _ = Assert.ThrowsExactly<InvalidOperationException>(() =>
            SystemAppPayloadBuilder.ValidateSymlinkTarget("usr/bin/zfs", "/etc/zfs/zpool.cache"));
        _ = Assert.ThrowsExactly<InvalidOperationException>(() =>
            SystemAppPayloadBuilder.ValidateSymlinkTarget("usr/bin/zfs", "../../etc/passwd"));
    }

    [TestMethod]
    [SupportedOSPlatform("linux")]
    public async Task CopyPackagePaths_Copies_Files_And_Symlinks_To_App_Payload()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "homeharbor-system-app-" + Guid.NewGuid().ToString("N"));
        var rootfs = Path.Combine(tempDir, "rootfs");
        var destination = Path.Combine(tempDir, "app");
        var zfs = Path.Combine(rootfs, "usr", "bin", "zfs");
        var link = Path.Combine(rootfs, "usr", "bin", "zpool");

        try
        {
            _ = Directory.CreateDirectory(Path.GetDirectoryName(zfs)!);
            await File.WriteAllTextAsync(zfs, "#!/bin/sh\n");
            File.SetUnixFileMode(zfs, UnixFileMode.UserRead | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            _ = File.CreateSymbolicLink(link, "zfs");

            SystemAppPayloadBuilder.CopyPackagePaths(rootfs, destination, ["/usr/bin/zfs", "/usr/bin/zpool"]);

            Assert.IsTrue(File.Exists(Path.Combine(destination, "usr", "bin", "zfs")));
            Assert.AreEqual("zfs", new FileInfo(Path.Combine(destination, "usr", "bin", "zpool")).LinkTarget);
            Assert.AreEqual(
                UnixFileMode.UserRead | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute,
                File.GetUnixFileMode(Path.Combine(destination, "usr", "bin", "zfs")));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task ExtractTarGzAsync_Allows_Usr_Payload()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "homeharbor-system-app-extract-" + Guid.NewGuid().ToString("N"));
        var archive = Path.Combine(tempDir, "payload.tar.gz");
        var destination = Path.Combine(tempDir, "out");

        try
        {
            _ = Directory.CreateDirectory(tempDir);
            await WriteTarGzAsync(archive, [("usr/bin/zfs", "#!/bin/sh\n")]);

            await SystemAppPayloadExtractor.ExtractTarGzAsync(archive, destination);

            Assert.IsTrue(File.Exists(Path.Combine(destination, "usr", "bin", "zfs")));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task ExtractTarGzAsync_Rejects_Payload_Outside_Usr()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "homeharbor-system-app-extract-" + Guid.NewGuid().ToString("N"));
        var archive = Path.Combine(tempDir, "payload.tar.gz");
        var destination = Path.Combine(tempDir, "out");

        try
        {
            _ = Directory.CreateDirectory(tempDir);
            await WriteTarGzAsync(archive, [("etc/passwd", "nope\n")]);

            var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                SystemAppPayloadExtractor.ExtractTarGzAsync(archive, destination));
            Assert.Contains("must stay under usr", ex.Message);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    private static async Task WriteTarGzAsync(string path, IReadOnlyList<(string Name, string Contents)> files)
    {
        await using var output = File.Create(path);
        await using var gzip = new GZipStream(output, CompressionLevel.SmallestSize);
        await using var writer = new TarWriter(gzip);
        foreach (var (Name, Contents) in files)
        {
            var bytes = Encoding.UTF8.GetBytes(Contents);
            var entry = new PaxTarEntry(TarEntryType.RegularFile, Name)
            {
                DataStream = new MemoryStream(bytes),
                Mode = UnixFileMode.UserRead | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute
            };
            await writer.WriteEntryAsync(entry);
        }
    }
}
