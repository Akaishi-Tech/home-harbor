using System.CommandLine;
using HomeHarbor.Tooling;

internal static partial class AgentProgram
{
    private static Command CreateBootStatePathCommand()
    {
        var espArgument = RequiredArgument("esp", "EFI system partition mount path.");
        var command = new Command("path", "Print the boot state path for an ESP.")
        {
            Arguments = { espArgument }
        };
        command.SetAction(parseResult =>
        {
            Console.WriteLine(BootState.PathForEsp(parseResult.GetValue(espArgument)!));
            return 0;
        });
        return command;
    }
}
