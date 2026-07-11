using System.Security.Cryptography;
using HomeHarbor.Tooling;

internal static class OtaEspBootAssetInstaller
{
    internal static void ValidateKernelAssets(
        string esp,
        string selectorSource,
        string selectorHash,
        string? fallbackSource,
        string? fallbackHash,
        string? mokManagerSource,
        string? mokManagerHash,
        string bootMode)
        => _ = BuildInstallPlan(
            esp,
            selectorSource,
            selectorHash,
            fallbackSource,
            fallbackHash,
            mokManagerSource,
            mokManagerHash,
            bootMode);

    internal static async Task InstallKernelAssetsAsync(
        ICommandRunner runner,
        string esp,
        string selectorSource,
        string selectorHash,
        string? fallbackSource,
        string? fallbackHash,
        string? mokManagerSource,
        string? mokManagerHash,
        string bootMode,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        if (dryRun)
        {
            return;
        }

        var plan = BuildInstallPlan(
            esp,
            selectorSource,
            selectorHash,
            fallbackSource,
            fallbackHash,
            mokManagerSource,
            mokManagerHash,
            bootMode);

        foreach (var asset in plan.Assets)
        {
            await AtomicInstallAsync(asset.Source, asset.Target, asset.Sha256, cancellationToken);
        }

        _ = (await runner.RunAsync("sync", ["-f", plan.EspRoot], cancellationToken: cancellationToken))
            .EnsureSuccess("failed to sync EFI system partition after boot selector update");
    }

    private static EspInstallPlan BuildInstallPlan(
        string esp,
        string selectorSource,
        string selectorHash,
        string? fallbackSource,
        string? fallbackHash,
        string? mokManagerSource,
        string? mokManagerHash,
        string bootMode)
    {
        if (bootMode is not ("raw-uki" or "secure-boot-raw-uki"))
        {
            throw new InvalidOperationException("unsupported boot mode for ESP selector update: " + bootMode);
        }

        var espRoot = RootPathGuard.RequireNoSymlinkComponents(
            Path.GetFullPath(esp),
            "EFI system partition",
            requireLeafDirectory: true);
        var selector = RequiredSource(selectorSource, selectorHash, "HomeHarborBoot.efi");
        var primaryTarget = TargetPath(espRoot, "EFI/HomeHarbor/HomeHarborBoot.efi", requireExisting: false);
        var assets = new List<EspAsset>();

        var fallbackTarget = TargetPath(espRoot, "EFI/BOOT/BOOTX64.EFI", requireExisting: true);
        if (fallbackTarget is not null)
        {
            var fallback = RequiredSource(fallbackSource, fallbackHash, "BOOTX64.EFI");
            assets.Add(new EspAsset(fallback.Path, fallbackTarget, fallback.Sha256));
        }

        if (bootMode == "secure-boot-raw-uki")
        {
            var grubTarget = TargetPath(espRoot, "EFI/BOOT/grubx64.efi", requireExisting: true);
            if (grubTarget is not null)
            {
                assets.Add(new EspAsset(selector.Path, grubTarget, selector.Sha256));
            }

            var mokTarget = TargetPath(espRoot, "EFI/BOOT/mmx64.efi", requireExisting: true);
            if (mokTarget is not null)
            {
                var mok = RequiredSource(mokManagerSource, mokManagerHash, "mmx64.efi");
                assets.Add(new EspAsset(mok.Path, mokTarget, mok.Sha256));
            }
        }

        // Replace the canonical selector last. A power loss while updating one of
        // the compatibility paths therefore leaves the normal boot path intact.
        assets.Add(new EspAsset(selector.Path, primaryTarget!, selector.Sha256));
        return new EspInstallPlan(espRoot, assets);
    }

    private static SourceAsset RequiredSource(string? path, string? expectedHash, string label)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(expectedHash))
        {
            throw new InvalidOperationException(label + " and its signed hash are required for this ESP update");
        }
        if (expectedHash.Length != 64 || expectedHash.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw new InvalidOperationException(label + " signed hash is not a lowercase SHA-256 digest");
        }

        var source = RootPathGuard.RequireNoSymlinkComponents(path, label + " OTA payload");
        if (!File.Exists(source) || (File.GetAttributes(source) & FileAttributes.Directory) != 0)
        {
            throw new InvalidOperationException(label + " OTA payload is missing or is not a regular file: " + source);
        }
        return new SourceAsset(source, expectedHash);
    }

    private static string? TargetPath(string espRoot, string relativePath, bool requireExisting)
    {
        var target = RootPathGuard.RequireChildPath(
            Path.Combine(espRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)),
            espRoot,
            "ESP boot asset");
        if (!Path.Exists(target))
        {
            return requireExisting ? null : target;
        }
        if ((File.GetAttributes(target) & FileAttributes.Directory) != 0)
        {
            throw new InvalidOperationException("ESP boot asset target is not a regular file: " + target);
        }
        return target;
    }

    private static async Task AtomicInstallAsync(
        string source,
        string target,
        string expectedHash,
        CancellationToken cancellationToken)
    {
        var parent = RootPathGuard.CreateDirectory(
            Path.GetDirectoryName(target) ?? throw new InvalidOperationException("ESP boot asset has no parent directory"),
            "ESP boot asset directory");
        target = RootPathGuard.RequireChildPath(target, parent, "ESP boot asset target");
        var temp = RootPathGuard.RequireChildPath(
            Path.Combine(parent, "." + Path.GetFileName(target) + ".homeharbor-ota-" + Guid.NewGuid().ToString("N")),
            parent,
            "ESP boot asset temporary file");

        try
        {
            await using (var input = new FileStream(
                             source,
                             FileMode.Open,
                             FileAccess.Read,
                             FileShare.Read,
                             bufferSize: 1024 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(
                             temp,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 1024 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await input.CopyToAsync(output, cancellationToken);
                await output.FlushAsync(cancellationToken);
                output.Flush(flushToDisk: true);
            }

            RequireHash(temp, expectedHash, "staged ESP boot asset");
            File.Move(temp, target, overwrite: true);
            RequireHash(target, expectedHash, "installed ESP boot asset");
        }
        finally
        {
            try
            {
                File.Delete(temp);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static void RequireHash(string path, string expected, string label)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var actual = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(label + " SHA-256 mismatch: " + path);
        }
    }

    private sealed record SourceAsset(string Path, string Sha256);

    private sealed record EspAsset(string Source, string Target, string Sha256);

    private sealed record EspInstallPlan(string EspRoot, IReadOnlyList<EspAsset> Assets);
}
