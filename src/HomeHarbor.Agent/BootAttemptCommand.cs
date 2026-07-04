using System.CommandLine;
using HomeHarbor.Tooling;

internal static partial class AgentProgram
{
    private static Command CreateBootAttemptCommand(ICommandRunner runner)
    {
        var stateDirOption = StringOption("--state-dir", "/var/lib/homeharbor/bootloop", "Bootloop state directory.");
        var espOption = StringOption("--esp", "/efi", "EFI system partition mount path.");
        var windowSecondsOption = NonNegativeIntOption("--window-seconds", 600, "Attempt window in seconds.");
        var thresholdOption = NonNegativeIntOption("--threshold", 3, "Attempt threshold.");
        var nowOption = NonNegativeLongOption("--now", () => DateTimeOffset.UtcNow.ToUnixTimeSeconds(), "Current UNIX timestamp.");
        var dryRunOption = new Option<bool>("--dry-run") { Description = "Do not reboot." };
        var command = new Command("boot-attempt", "Record a boot attempt and request recovery after repeated failures.")
        {
            Options = { stateDirOption, espOption, windowSecondsOption, thresholdOption, nowOption, dryRunOption }
        };
        command.SetAction((parseResult, cancellationToken) => BootAttemptAsync(
            new BootAttemptOptions(
                parseResult.GetValue(stateDirOption)!,
                parseResult.GetValue(espOption)!,
                parseResult.GetValue(windowSecondsOption),
                parseResult.GetValue(thresholdOption),
                parseResult.GetValue(nowOption),
                parseResult.GetValue(dryRunOption)),
            runner,
            cancellationToken));
        return command;
    }
}
