using System.CommandLine;
using HomeHarbor.Tooling;

internal static partial class AgentProgram
{
    private static Command CreateVerifyOtaManifestCommand(ICommandRunner runner)
    {
        var manifestArgument = RequiredArgument("manifest", "OTA manifest path.");
        var publicKeyArgument = RequiredArgument("public-key", "Ed25519 public key PEM path.");
        var command = new Command("verify-ota-manifest", "Verify an OTA manifest signature.")
        {
            Arguments = { manifestArgument, publicKeyArgument }
        };
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            await new OtaManifestVerifier(runner).VerifyAsync(
                parseResult.GetValue(manifestArgument)!,
                parseResult.GetValue(publicKeyArgument)!,
                cancellationToken);
            Console.WriteLine("OTA manifest signature verified: " + parseResult.GetValue(manifestArgument));
            return 0;
        });
        return command;
    }
}
