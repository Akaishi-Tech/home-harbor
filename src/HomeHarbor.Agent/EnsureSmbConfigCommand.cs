using System.CommandLine;
using HomeHarbor.Tooling;

internal static partial class AgentProgram
{
    private static Command CreateEnsureSmbConfigCommand(ICommandRunner runner)
        => SimpleCommand("ensure-smb-config", (runner, cancellationToken) => EnsureSmbConfigAsync(runner, cancellationToken), runner);
}
