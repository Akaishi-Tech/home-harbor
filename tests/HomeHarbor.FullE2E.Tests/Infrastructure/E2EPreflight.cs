namespace HomeHarbor.FullE2E.Tests.Infrastructure;

internal sealed class E2EPreflight(ProcessRunner processes)
{
    public async Task VerifyAsync(E2EOptions options, CancellationToken cancellationToken)
    {
        if (!File.Exists(options.IsoPath))
        {
            throw new AssertFailedException(
                $"FullE2E ISO not found: {options.IsoPath}. " +
                "Build the channel OTA and ISO artifacts first, or set HOMEHARBOR_E2E_ISO.");
        }

        _ = await processes.RunRequiredAsync(
            "bash",
            [
                "-lc",
                "for tool in sudo virsh virt-install qemu-img curl jq python3 sha256sum stat fastboot; do command -v \"$tool\" >/dev/null || { echo \"required tool not found: $tool\" >&2; exit 1; }; done"
            ],
            RepoPaths.Root,
            cancellationToken: cancellationToken);

        _ = await processes.RunRequiredAsync(
            "sudo",
            ["-n", "true"],
            RepoPaths.Root,
            timeout: TimeSpan.FromSeconds(10),
            cancellationToken: cancellationToken);

        _ = await processes.RunRequiredAsync(
            "virsh",
            ["-c", options.Connect, "list", "--all"],
            RepoPaths.Root,
            timeout: TimeSpan.FromSeconds(20),
            cancellationToken: cancellationToken);

        var defaultNetwork = await ProcessRunner.RunAsync(
            "virsh",
            ["-c", options.Connect, "net-info", "default"],
            RepoPaths.Root,
            timeout: TimeSpan.FromSeconds(20),
            cancellationToken: cancellationToken);
        if (defaultNetwork.ExitCode != 0)
        {
            throw new AssertFailedException(
                $"{DefaultNetworkFailureMessage(options.Connect)}\nSTDOUT:\n{defaultNetwork.Stdout}\nSTDERR:\n{defaultNetwork.Stderr}");
        }

        if (!IsDefaultNetworkActive(defaultNetwork.Stdout))
        {
            throw new AssertFailedException(
                $"{DefaultNetworkFailureMessage(options.Connect)}\nnet-info default output:\n{defaultNetwork.Stdout}");
        }
    }

    private static bool IsDefaultNetworkActive(string netInfo)
    {
        foreach (var line in netInfo.Split('\n', StringSplitOptions.TrimEntries))
        {
            var separator = line.IndexOf(':', StringComparison.Ordinal);
            if (separator < 0)
            {
                continue;
            }

            var name = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            if (string.Equals(name, "Active", StringComparison.OrdinalIgnoreCase))
            {
                return string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
            }
        }

        return false;
    }

    private static string DefaultNetworkFailureMessage(string connect)
        => "libvirt default network must exist and be active for FullE2E. " +
            $"Check it with: virsh -c {connect} net-info default\n" +
            $"Start it with: virsh -c {connect} net-start default\n" +
            "If this is not the intended libvirt connection, set HOMEHARBOR_E2E_CONNECT.";
}
