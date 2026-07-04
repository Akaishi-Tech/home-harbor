using System.CommandLine;
using HomeHarbor.Tooling;

internal static partial class AgentProgram
{
    private static Command CreateBootStateSetDefaultCommand()
    {
        var espArgument = RequiredArgument("esp", "EFI system partition mount path.");
        var slotArgument = RequiredArgument("slot", "Default boot slot.");
        var rootSlotArgument = new Argument<string?>("root-slot")
        {
            Arity = ArgumentArity.ZeroOrOne,
            Description = "Default root slot."
        };
        var command = new Command("set-default", "Set the default boot slot.")
        {
            Arguments = { espArgument, slotArgument, rootSlotArgument }
        };
        command.SetAction(parseResult =>
        {
            BootState.SetDefault(
                parseResult.GetValue(espArgument)!,
                parseResult.GetValue(slotArgument)!,
                parseResult.GetValue(rootSlotArgument));
            return 0;
        });
        return command;
    }
}
