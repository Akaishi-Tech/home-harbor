using System.CommandLine;
using HomeHarbor.Tooling;

internal static partial class AgentProgram
{
    private static Command CreatePostgresInitCommand(ICommandRunner runner)
        => SimpleCommand("postgres-init", (runner, cancellationToken) => PostgresInitAsync(runner, cancellationToken), runner);
}
