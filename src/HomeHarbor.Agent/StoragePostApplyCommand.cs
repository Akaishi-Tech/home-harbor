using System.CommandLine;
using HomeHarbor.Tooling;

internal static partial class AgentProgram
{
    private static Command CreateStoragePostApplyCommand(ICommandRunner runner)
        => SimpleCommand("storage-postapply", (runner, cancellationToken) => StoragePostApplyAsync(runner, cancellationToken), runner);
}
