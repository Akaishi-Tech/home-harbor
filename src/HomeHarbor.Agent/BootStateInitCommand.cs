using System.CommandLine;
using HomeHarbor.Tooling;

internal static partial class AgentProgram
{
    private static Command CreateBootStateInitCommand()
    {
        var espArgument = RequiredArgument("esp", "EFI system partition mount path.");
        var slotArgument = OptionalArgument("slot", "A", "Default boot slot.");
        var rootSlotArgument = new Argument<string?>("root-slot")
        {
            Arity = ArgumentArity.ZeroOrOne,
            Description = "Default root slot."
        };
        var recoverySlotArgument = new Argument<string?>("recovery-slot")
        {
            Arity = ArgumentArity.ZeroOrOne,
            Description = "Recovery slot."
        };
        var command = new Command("init", "Initialize boot state.")
        {
            Arguments = { espArgument, slotArgument, rootSlotArgument, recoverySlotArgument }
        };
        command.SetAction(parseResult =>
        {
            var slot = parseResult.GetValue(slotArgument)!;
            BootState.Initialize(
                parseResult.GetValue(espArgument)!,
                slot,
                parseResult.GetValue(rootSlotArgument) ?? slot,
                parseResult.GetValue(recoverySlotArgument) ?? slot);
            return 0;
        });
        return command;
    }
}
