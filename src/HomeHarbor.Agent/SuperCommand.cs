using System.CommandLine;
using HomeHarbor.Tooling;

internal static partial class AgentProgram
{
    private static Command CreateSuperCommand(ICommandRunner runner)
    {
        var command = new Command("super", "Manage Android dynamic super partition mappings.");
        command.Subcommands.Add(CreateSuperTableCommand(runner));
        command.Subcommands.Add(CreateSuperCreateCommand(runner));
        command.Subcommands.Add(CreateSuperRemoveCommand(runner));
        return command;
    }
}
