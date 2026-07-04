using System.CommandLine;
using HomeHarbor.Tooling;

internal static partial class AgentProgram
{
    private static Command CreateBootSuccessCommand(ICommandRunner runner)
    {
        var stateDirOption = StringOption("--state-dir", "/var/lib/homeharbor/bootloop", "Bootloop state directory.");
        var timeoutSecondsOption = NonNegativeIntOption("--timeout-seconds", 120, "Health wait timeout in seconds.");
        var healthUrlOption = StringOption("--health-url", "/api/system/health", "Health endpoint path or URL.");
        var apiUrlOption = StringOption("--api-url", "http://homeharbor", "HomeHarbor API URL.");
        var apiSocketOption = NullableStringOption("--api-socket", "HomeHarbor API Unix socket path.");
        var noApiSocketOption = new Option<bool>("--no-api-socket") { Description = "Use TCP instead of the API Unix socket." };
        var dryRunOption = new Option<bool>("--dry-run") { Description = "Do not bless the boot." };
        var command = new Command("boot-success", "Mark a boot successful after the API health endpoint responds.")
        {
            Options =
            {
                stateDirOption,
                timeoutSecondsOption,
                healthUrlOption,
                apiUrlOption,
                apiSocketOption,
                noApiSocketOption,
                dryRunOption
            }
        };
        command.SetAction((parseResult, cancellationToken) => BootSuccessAsync(
            new BootSuccessOptions(
                parseResult.GetValue(stateDirOption)!,
                parseResult.GetValue(timeoutSecondsOption),
                parseResult.GetValue(healthUrlOption)!,
                parseResult.GetValue(apiUrlOption)!,
                ApiSocketValue(parseResult, apiUrlOption, apiSocketOption, noApiSocketOption),
                parseResult.GetValue(dryRunOption)),
            runner,
            cancellationToken));
        return command;
    }
}
