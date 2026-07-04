using System.CommandLine;
using HomeHarbor.Tooling;

internal static partial class AgentProgram
{
    private static Command CreateApplySystemAppsCommand(ICommandRunner runner)
        => SimpleCommand("apply-system-apps", (runner, cancellationToken) => ApplySystemAppsAsync(runner, cancellationToken), runner);
}
