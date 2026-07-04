using System.CommandLine;
using HomeHarbor.Tooling;

internal static partial class AgentProgram
{
    private static Command CreateApplySmbCommand(ICommandRunner runner)
        => SimpleCommand("apply-smb", (runner, cancellationToken) => ApplySmbAsync(runner, cancellationToken), runner);
}
