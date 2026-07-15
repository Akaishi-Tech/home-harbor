using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HomeHarbor.Tooling;

namespace HomeHarbor.Tests;

[TestClass]
public sealed class OtaBootSelectorTests
{
    [TestMethod]
    public void System_Bundle_Must_Match_The_Running_Kernel_Sequence()
    {
        OtaApplyCommand.RequireSystemBundleMatchesRunningKernel(42, 42);

        var newer = Assert.ThrowsExactly<InvalidOperationException>(() =>
            OtaApplyCommand.RequireSystemBundleMatchesRunningKernel(43, 42));
        Assert.Contains("matching kernel bundle first", newer.Message);
        _ = Assert.ThrowsExactly<InvalidOperationException>(() =>
            OtaApplyCommand.RequireSystemBundleMatchesRunningKernel(41, 42));
    }

    [TestMethod]
    public void Kernel_Manifest_Requires_A_Signed_Boot_Selector_Hash()
    {
        using var missing = JsonDocument.Parse(KernelManifest(includeBootloader: false));
        var exception = Assert.ThrowsExactly<InvalidOperationException>(() =>
            OtaManifestVerifier.CanonicalPayload(missing.RootElement));
        Assert.Contains("bootloaderHash", exception.Message);

        using var complete = JsonDocument.Parse(KernelManifest(includeBootloader: true));
        Assert.Contains("\"bootloaderHash\":\"selector\"", OtaManifestVerifier.CanonicalPayload(complete.RootElement));
    }

    [TestMethod]
    public void Kernel_Esp_Install_Precedes_BootNext_And_Pending_State()
    {
        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot(),
            "tools",
            "system-build",
            "external",
            "system-utils",
            "src",
            "HomeHarbor.Tooling",
            "OtaApplyCommand.cs"));
        var install = source.IndexOf("InstallKernelAssetsAsync", StringComparison.Ordinal);
        var bootNext = source.IndexOf("EfiBootVariables.SetOneShotAsync", StringComparison.Ordinal);
        var pending = source.IndexOf("WritePendingAsync(options.StateDir", StringComparison.Ordinal);

        Assert.IsGreaterThanOrEqualTo(0, install);
        Assert.IsGreaterThan(install, bootNext);
        Assert.IsGreaterThan(install, pending);
    }

    [TestMethod]
    public async Task Secure_Kernel_Ota_Atomically_Updates_All_Existing_Esp_Boot_Paths()
    {
        var root = CreateTempDirectory();
        try
        {
            var esp = Directory.CreateDirectory(Path.Combine(root, "esp")).FullName;
            var homeHarbor = Directory.CreateDirectory(Path.Combine(esp, "EFI", "HomeHarbor")).FullName;
            var boot = Directory.CreateDirectory(Path.Combine(esp, "EFI", "BOOT")).FullName;
            var selector = WritePayload(root, "HomeHarborBoot.efi", "new selector");
            var fallback = WritePayload(root, "BOOTX64.EFI", "new shim");
            var mok = WritePayload(root, "mmx64.efi", "new MokManager");
            await File.WriteAllTextAsync(Path.Combine(homeHarbor, "HomeHarborBoot.efi"), "old selector");
            await File.WriteAllTextAsync(Path.Combine(boot, "BOOTX64.EFI"), "old shim");
            await File.WriteAllTextAsync(Path.Combine(boot, "grubx64.efi"), "old selector copy");
            await File.WriteAllTextAsync(Path.Combine(boot, "mmx64.efi"), "old MokManager");
            var runner = new RecordingRunner();

            await OtaEspBootAssetInstaller.InstallKernelAssetsAsync(
                runner,
                esp,
                selector,
                Hash(selector),
                fallback,
                Hash(fallback),
                mok,
                Hash(mok),
                "secure-boot-raw-uki",
                dryRun: false,
                CancellationToken.None);

            Assert.AreEqual("new selector", await File.ReadAllTextAsync(Path.Combine(homeHarbor, "HomeHarborBoot.efi")));
            Assert.AreEqual("new selector", await File.ReadAllTextAsync(Path.Combine(boot, "grubx64.efi")));
            Assert.AreEqual("new shim", await File.ReadAllTextAsync(Path.Combine(boot, "BOOTX64.EFI")));
            Assert.AreEqual("new MokManager", await File.ReadAllTextAsync(Path.Combine(boot, "mmx64.efi")));
            Assert.HasCount(1, runner.Calls);
            Assert.AreEqual("sync", runner.Calls[0].FileName);
            CollectionAssert.AreEqual(new[] { "-f", esp }, runner.Calls[0].Arguments);
            Assert.IsEmpty(Directory.EnumerateFiles(esp, "*.homeharbor-ota-*", SearchOption.AllDirectories));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task Esp_Installer_Dry_Run_Does_Not_Write_Or_Sync()
    {
        var root = CreateTempDirectory();
        try
        {
            var esp = Directory.CreateDirectory(Path.Combine(root, "esp")).FullName;
            var targetDir = Directory.CreateDirectory(Path.Combine(esp, "EFI", "HomeHarbor")).FullName;
            var target = Path.Combine(targetDir, "HomeHarborBoot.efi");
            await File.WriteAllTextAsync(target, "unchanged");
            var runner = new RecordingRunner();

            await OtaEspBootAssetInstaller.InstallKernelAssetsAsync(
                runner,
                esp,
                Path.Combine(root, "missing-selector"),
                new string('a', 64),
                null,
                null,
                null,
                null,
                "raw-uki",
                dryRun: true,
                CancellationToken.None);

            Assert.AreEqual("unchanged", await File.ReadAllTextAsync(target));
            Assert.IsEmpty(runner.Calls);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void Esp_Preflight_Rejects_Symlink_Targets_And_Missing_Existing_Companions()
    {
        var root = CreateTempDirectory();
        try
        {
            var esp = Directory.CreateDirectory(Path.Combine(root, "esp")).FullName;
            var homeHarbor = Directory.CreateDirectory(Path.Combine(esp, "EFI", "HomeHarbor")).FullName;
            var boot = Directory.CreateDirectory(Path.Combine(esp, "EFI", "BOOT")).FullName;
            var selector = WritePayload(root, "selector.efi", "selector");
            var external = WritePayload(root, "external.efi", "external");
            _ = File.CreateSymbolicLink(Path.Combine(homeHarbor, "HomeHarborBoot.efi"), external);

            var symlink = Assert.ThrowsExactly<InvalidOperationException>(() =>
                OtaEspBootAssetInstaller.ValidateKernelAssets(
                    esp, selector, Hash(selector), null, null, null, null, "raw-uki"));
            Assert.Contains("symbolic link", symlink.Message);

            File.Delete(Path.Combine(homeHarbor, "HomeHarborBoot.efi"));
            File.WriteAllText(Path.Combine(boot, "BOOTX64.EFI"), "installed fallback");
            var missingFallback = Assert.ThrowsExactly<InvalidOperationException>(() =>
                OtaEspBootAssetInstaller.ValidateKernelAssets(
                    esp, selector, Hash(selector), null, null, null, null, "raw-uki"));
            Assert.Contains("BOOTX64.EFI", missingFallback.Message);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string KernelManifest(bool includeBootloader)
        => $$"""
            {
              "schemaVersion": "1",
              "type": "kernel-only",
              "packageKind": "kernel",
              "releaseSequence": 42,
              "version": "0.2.0",
              "channel": "dev",
              "kernelChannel": "generic",
              "createdAt": "2026-07-11T00:00:00Z",
              "bootMode": "raw-uki",
              "kernelRelease": "6.18.0-homeharbor",
              "modulesHash": "modules",
              "firmwareHash": "firmware",
              "recoveryHash": "recovery",
              "bootHash": "boot"{{(includeBootloader ? ",\n  \"bootloaderHash\": \"selector\"" : string.Empty)}}
            }
            """;

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "homeharbor-ota-esp-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(path);
        return path;
    }

    private static string RepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "HomeHarbor.slnx")))
            {
                return current.FullName;
            }
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Repository root not found");
    }

    private static string WritePayload(string root, string name, string content)
    {
        var path = Path.Combine(root, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static string Hash(string path)
        => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private sealed class RecordingRunner : ICommandRunner
    {
        public List<Call> Calls { get; } = [];

        public Task<CommandResult> RunAsync(
            string fileName,
            IEnumerable<string> arguments,
            CommandRunOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(new Call(fileName, [.. arguments]));
            return Task.FromResult(new CommandResult(0, string.Empty, string.Empty, fileName));
        }
    }

    private sealed record Call(string FileName, string[] Arguments);
}
