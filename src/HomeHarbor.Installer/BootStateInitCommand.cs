using System.CommandLine;
using HomeHarbor.Tooling;

internal static partial class InstallerProgram
{
    private static Command CreateBootStateInitCommand()
    {
        var espArgument = RequiredArgument("esp", "EFI system partition mount path.");
        var slotArgument = OptionalArgument("slot", "A", "Default boot slot.");
        var rootSlotArgument = OptionalNullableArgument("root-slot", "Default root slot.");
        var recoverySlotArgument = OptionalNullableArgument("recovery-slot", "Recovery slot.");
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
