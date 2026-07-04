using System.CommandLine;
using HomeHarbor.Tooling;

internal static partial class AgentProgram
{
    private static Command CreateEnsureCaddyConfigCommand(ICommandRunner runner)
        => SimpleCommand("ensure-caddy-config", (runner, cancellationToken) => EnsureCaddyConfigAsync(runner, cancellationToken), runner);
}
