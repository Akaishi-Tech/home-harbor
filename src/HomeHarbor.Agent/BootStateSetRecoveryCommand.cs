using System.CommandLine;
using HomeHarbor.Tooling;

internal static partial class AgentProgram
{
    private static Command CreateBootStateSetRecoveryCommand()
    {
        var espArgument = RequiredArgument("esp", "EFI system partition mount path.");
        var slotArgument = RequiredArgument("slot", "Recovery slot.");
        var command = new Command("set-recovery", "Set the recovery slot.")
        {
            Arguments = { espArgument, slotArgument }
        };
        command.SetAction(parseResult =>
        {
            BootState.SetRecovery(parseResult.GetValue(espArgument)!, parseResult.GetValue(slotArgument)!);
            return 0;
        });
        return command;
    }
}
