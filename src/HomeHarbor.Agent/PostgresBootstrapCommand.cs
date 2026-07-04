using System.CommandLine;
using HomeHarbor.Tooling;

internal static partial class AgentProgram
{
    private static Command CreatePostgresBootstrapCommand(ICommandRunner runner)
        => SimpleCommand("postgres-bootstrap", (runner, cancellationToken) => PostgresBootstrapAsync(runner, cancellationToken), runner);
}
