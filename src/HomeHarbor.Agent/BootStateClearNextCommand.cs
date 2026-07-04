using System.CommandLine;
using HomeHarbor.Tooling;

internal static partial class AgentProgram
{
    private static Command CreateBootStateClearNextCommand(ICommandRunner runner)
    {
        var espArgument = RequiredArgument("esp", "Ignored EFI system partition mount path kept for CLI compatibility.");
        var command = new Command("clear-next", "Clear EFI one-shot boot variables.")
        {
            Arguments = { espArgument }
        };
        command.SetAction(async (_, cancellationToken) =>
        {
            await EfiBootVariables.ClearOneShotAsync(runner, cancellationToken);
            return 0;
        });
        return command;
    }
}
