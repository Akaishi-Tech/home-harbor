using System.CommandLine;

internal static partial class InstallerProgram
{
    private static Command CreateBootStateCommand()
    {
        var command = new Command("boot-state", "Manage HomeHarbor boot state.");
        command.Subcommands.Add(CreateBootStateInitCommand());
        command.Subcommands.Add(CreateBootStateSetDefaultCommand());
        command.Subcommands.Add(CreateBootStateSetOneshotCommand());
        command.Subcommands.Add(CreateBootStateSetRecoveryCommand());
        command.Subcommands.Add(CreateBootStateClearNextCommand());
        command.Subcommands.Add(CreateBootStatePathCommand());
        return command;
    }
}
