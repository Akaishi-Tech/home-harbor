using System.CommandLine;
using HomeHarbor.Tooling;

internal static partial class AgentProgram
{
    private static Command CreateBootStateSetOneshotCommand(ICommandRunner runner)
    {
        var espArgument = RequiredArgument("esp", "EFI system partition mount path.");
        var bootSlotArgument = RequiredArgument("boot-slot", "One-shot boot slot.");
        var rootSlotArgument = RequiredArgument("root-slot", "Root slot, or 'recovery'.");
        var modeArgument = OptionalArgument("mode", "normal", "Boot mode.");
        var command = new Command("set-oneshot", "Set EFI one-shot boot variables.")
        {
            Arguments = { espArgument, bootSlotArgument, rootSlotArgument, modeArgument }
        };
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var bootSlot = parseResult.GetValue(bootSlotArgument)!;
            var rootSlot = parseResult.GetValue(rootSlotArgument)!;
            if (string.Equals(rootSlot, "recovery", StringComparison.OrdinalIgnoreCase))
            {
                await EfiBootVariables.SetOneShotAsync(runner, bootSlot, bootSlot, "recovery", cancellationToken);
                return 0;
            }

            await EfiBootVariables.SetOneShotAsync(runner, bootSlot, rootSlot, parseResult.GetValue(modeArgument)!, cancellationToken);
            return 0;
        });
        return command;
    }
}
