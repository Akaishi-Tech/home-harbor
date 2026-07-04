using System.CommandLine;
using HomeHarbor.Tooling;

internal static partial class AgentProgram
{
    private static Command CreateApplyContainersCommand(ICommandRunner runner)
        => SimpleCommand("apply-containers", (runner, cancellationToken) => ApplyContainersAsync(runner, cancellationToken), runner);
}
