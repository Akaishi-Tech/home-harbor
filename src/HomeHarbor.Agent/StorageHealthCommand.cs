using System.CommandLine;
using HomeHarbor.Tooling;

internal static partial class AgentProgram
{
    private static Command CreateStorageHealthCommand(ICommandRunner runner)
        => SimpleCommand("storage-health", (_, cancellationToken) => StorageHealthAsync(cancellationToken), runner);
}
