namespace HomeHarbor.Tooling;

public sealed class KernelModuleDetector(ICommandRunner? runner = null)
{
    private readonly ICommandRunner _runner = runner ?? new ProcessCommandRunner();

    public async Task<bool> IsModuleAvailableAsync(
        string moduleName,
        string modulesRoot = "/usr/lib/modules",
        string? kernelRelease = null,
        CancellationToken cancellationToken = default)
    {
        var module = HomeHarborAppManifestVerifier.ValidateModuleName(moduleName);
        var modinfo = await _runner.RunAsync(
            "modinfo",
            ["-n", module],
            new CommandRunOptions(ThrowOnStartFailure: false),
            cancellationToken);
        if (modinfo.ExitCode == 0)
        {
            var path = modinfo.Stdout.Trim();
            if (!string.IsNullOrWhiteSpace(path) &&
                !path.Contains("not found", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        kernelRelease ??= await CurrentKernelReleaseAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(kernelRelease))
        {
            return false;
        }

        var root = Path.Combine(modulesRoot, kernelRelease.Trim());
        if (!Directory.Exists(root))
        {
            return false;
        }

        var names = new[]
        {
            module + ".ko",
            module + ".ko.gz",
            module + ".ko.xz",
            module + ".ko.zst"
        };

        return Directory.EnumerateFiles(root, module + ".ko*", SearchOption.AllDirectories)
            .Any(path => names.Contains(Path.GetFileName(path), StringComparer.Ordinal));
    }

    private async Task<string?> CurrentKernelReleaseAsync(CancellationToken cancellationToken)
    {
        var uname = await _runner.RunAsync(
            "uname",
            ["-r"],
            new CommandRunOptions(ThrowOnStartFailure: false),
            cancellationToken);
        return uname.ExitCode == 0 ? uname.Stdout.Trim() : null;
    }
}
