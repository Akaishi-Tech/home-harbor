namespace HomeHarbor.Tooling;

public sealed class RootlessBuildExecutor(ICommandRunner runner)
{
    private static readonly string[] MappedRootPrefix =
    [
        "--fork",
        "--pid",
        "--mount",
        "--propagation",
        "unchanged",
        "--setgroups",
        "allow",
        "--map-auto",
        "--map-root-user",
        "--setuid",
        "0",
        "--setgid",
        "0"
    ];

    private readonly ICommandRunner _runner = runner;

    public async Task RequireReadyAsync(CancellationToken cancellationToken = default)
    {
        await RequireNonRootAsync(cancellationToken);
        foreach (var tool in new[] { "fakeroot", "unshare", "pacstrap", "arch-chroot" })
        {
            await NeedAsync(tool, cancellationToken);
        }

        await RequireHelpOptionAsync("pacstrap", "-N", cancellationToken);
        await RequireHelpOptionAsync("arch-chroot", "-N", cancellationToken);
        await RequireMappedRootAsync(cancellationToken);
    }

    public async Task RequireNonRootAsync(CancellationToken cancellationToken = default)
    {
        var result = await _runner.RunAsync("id", ["-u"], cancellationToken: cancellationToken);
        _ = result.EnsureSuccess("could not determine current uid");
        if (string.Equals(result.Stdout.Trim(), "0", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("image builders must run as a normal user, not as real root");
        }
    }

    public Task<CommandResult> RunPacstrapAsync(
        string rootfs,
        IEnumerable<string> packages,
        CommandRunOptions? options = null,
        CancellationToken cancellationToken = default)
        => _runner.RunAsync(
            "pacstrap",
            ["-N", "-c", rootfs, .. packages],
            options,
            cancellationToken);

    public Task<CommandResult> RunMappedRootAsync(
        string fileName,
        IEnumerable<string> arguments,
        CommandRunOptions? options = null,
        CancellationToken cancellationToken = default)
        => _runner.RunAsync(
            "unshare",
            MappedRootArguments(fileName, arguments),
            options,
            cancellationToken);

    public Task<CommandResult> RunMappedChrootAsync(
        string rootfs,
        string fileName,
        IEnumerable<string> arguments,
        CommandRunOptions? options = null,
        CancellationToken cancellationToken = default)
        => _runner.RunAsync(
            "arch-chroot",
            ["-N", rootfs, fileName, .. arguments],
            options,
            cancellationToken);

    public static IReadOnlyList<string> MappedRootArguments(string fileName, IEnumerable<string> arguments)
        => [.. MappedRootPrefix, fileName, .. arguments];

    private async Task NeedAsync(string command, CancellationToken cancellationToken)
    {
        var result = await _runner.RunAsync(
            "sh",
            ["-c", "command -v \"$1\" >/dev/null 2>&1", "sh", command],
            new CommandRunOptions(ThrowOnStartFailure: false),
            cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException("missing required rootless build tool: " + command);
        }
    }

    private async Task RequireHelpOptionAsync(string command, string option, CancellationToken cancellationToken)
    {
        var result = await _runner.RunAsync(
            command,
            ["-h"],
            new CommandRunOptions(ThrowOnStartFailure: false),
            cancellationToken);
        if (result.ExitCode == 127 || !result.CombinedOutput.Contains(option, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{command} must support {option} rootless unshare mode");
        }
    }

    private async Task RequireMappedRootAsync(CancellationToken cancellationToken)
    {
        var result = await RunMappedRootAsync(
            "id",
            ["-u"],
            new CommandRunOptions(ThrowOnStartFailure: false),
            cancellationToken);
        if (result.ExitCode != 0 || !string.Equals(result.Stdout.Trim(), "0", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "rootless image builds require working unprivileged user namespaces with subuid/subgid mappings. " +
                "Enable user namespaces and configure /etc/subuid and /etc/subgid for the build user." +
                (string.IsNullOrWhiteSpace(result.CombinedOutput) ? string.Empty : Environment.NewLine + result.CombinedOutput.Trim()));
        }
    }
}
