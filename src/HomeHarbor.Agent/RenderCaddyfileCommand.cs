using System.CommandLine;
using HomeHarbor.Tooling;

internal static partial class AgentProgram
{
    private static Command CreateRenderCaddyfileCommand(ICommandRunner runner)
        => SimpleCommand("render-caddyfile", (runner, cancellationToken) => RenderCaddyfileAsync(runner, cancellationToken), runner);
}
