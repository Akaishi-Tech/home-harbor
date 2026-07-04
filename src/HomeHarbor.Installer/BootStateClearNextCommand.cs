using System.CommandLine;
using HomeHarbor.Tooling;

internal static partial class InstallerProgram
{
    private static Command CreateBootStateClearNextCommand()
    {
        var espArgument = RequiredArgument("esp", "Ignored EFI system partition mount path kept for CLI compatibility.");
        var command = new Command("clear-next", "Clear EFI one-shot boot variables.")
        {
            Arguments = { espArgument }
        };
        command.SetAction(async (_, cancellationToken) =>
        {
            await EfiBootVariables.ClearOneShotAsync(new ProcessCommandRunner(), cancellationToken);
            return 0;
        });
        return command;
    }
}
