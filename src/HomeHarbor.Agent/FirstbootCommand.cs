using System.CommandLine;
using HomeHarbor.Tooling;

internal static partial class AgentProgram
{
    private static Command CreateFirstbootCommand(ICommandRunner runner)
        => SimpleCommand("firstboot", (runner, cancellationToken) => FirstbootAsync(runner, cancellationToken), runner);
}
