using System.CommandLine;
using HomeHarbor.Tooling;

internal static partial class AgentProgram
{
    private static Command CreateConsumeSetupBootstrapCommand(ICommandRunner runner)
        => SimpleCommand("consume-setup-bootstrap", ConsumeSetupBootstrapAsync, runner);

    private static async Task<int> ConsumeSetupBootstrapAsync(ICommandRunner runner, CancellationToken cancellationToken)
    {
        var requestPath = SetupPath(
            "HOMEHARBOR_SETUP_BOOTSTRAP_CONSUME_REQUEST_PATH",
            "HomeHarbor__Setup__ConsumeRequestPath",
            SetupBootstrapCode.DefaultConsumeRequestPath);
        var codePath = SetupPath(
            "HOMEHARBOR_SETUP_BOOTSTRAP_CODE_PATH",
            "HomeHarbor__Setup__BootstrapCodePath",
            SetupBootstrapCode.DefaultCodePath);
        var completePath = SetupPath(
            "HOMEHARBOR_SETUP_BOOTSTRAP_COMPLETE_PATH",
            "HomeHarbor__Setup__BootstrapCompletePath",
            SetupBootstrapCode.DefaultCompletePath);
        await SetupBootstrapCode.ConsumeAsync(runner, requestPath, codePath, completePath, cancellationToken);
        return 0;
    }
}
