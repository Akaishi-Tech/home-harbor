using System.CommandLine;
using HomeHarbor.Tooling;

internal static partial class AgentProgram
{
    private static Command CreateStorageApplyCommand(ICommandRunner runner)
        => SimpleCommand("storage-apply", (runner, cancellationToken) => StorageApplyAsync(runner, cancellationToken), runner);
}
