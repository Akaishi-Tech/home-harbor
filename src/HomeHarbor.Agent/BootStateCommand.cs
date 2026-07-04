using System.CommandLine;
using HomeHarbor.Tooling;

internal static partial class AgentProgram
{
    private static Command CreateBootStateCommand(ICommandRunner runner)
    {
        var command = new Command("boot-state", "Manage HomeHarbor boot state.");
        command.Subcommands.Add(CreateBootStateInitCommand());
        command.Subcommands.Add(CreateBootStateSetDefaultCommand());
        command.Subcommands.Add(CreateBootStateSetOneshotCommand(runner));
        command.Subcommands.Add(CreateBootStateSetRecoveryCommand());
        command.Subcommands.Add(CreateBootStateClearNextCommand(runner));
        command.Subcommands.Add(CreateBootStatePathCommand());
        return command;
    }
}
