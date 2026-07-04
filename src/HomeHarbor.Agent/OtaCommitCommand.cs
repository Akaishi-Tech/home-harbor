using System.CommandLine;
using HomeHarbor.Tooling;

internal static partial class AgentProgram
{
    private static Command CreateOtaCommitCommand(ICommandRunner runner)
    {
        var stateDirOption = StringOption("--state-dir", "/var/lib/homeharbor/ota", "OTA state directory.");
        var espOption = StringOption("--esp", "/efi", "EFI system partition mount path.");
        var bootEnvOption = StringOption("--boot-env", "/run/homeharbor/boot.env", "Current boot environment file.");
        var runDirOption = StringOption("--run-dir", "/run/homeharbor", "Runtime state directory.");
        var command = new Command("ota-commit", "Commit a successfully booted OTA.")
        {
            Options = { stateDirOption, espOption, bootEnvOption, runDirOption }
        };
        command.SetAction((parseResult, cancellationToken) => OtaCommitAsync(
            new OtaCommitOptions(
                parseResult.GetValue(stateDirOption)!,
                parseResult.GetValue(espOption)!,
                parseResult.GetValue(bootEnvOption)!,
                parseResult.GetValue(runDirOption)!),
            runner,
            cancellationToken));
        return command;
    }
}
