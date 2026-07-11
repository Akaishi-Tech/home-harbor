using System.CommandLine;
using HomeHarbor.Tooling;

internal static partial class AgentProgram
{
    private static Command CreateDisplayTlsTrustCommand()
    {
        var certificate = StringOption(
            "--certificate",
            CaddyTrustConfiguration.RootCertificatePath,
            "Caddy internal root CA certificate path.");
        var consoles = StringOption(
            "--consoles",
            "/dev/console,/dev/tty1,/dev/ttyS0",
            "Comma-separated physical console paths.");
        var command = new Command("display-tls-trust", "Display the authenticated HomeHarbor CA fingerprint on physical consoles.");
        command.Options.Add(certificate);
        command.Options.Add(consoles);
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var consolePaths = parseResult.GetValue(consoles)!
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var fingerprint = await TlsTrustBootstrap.DisplayAsync(
                parseResult.GetValue(certificate)!,
                consolePaths,
                cancellationToken);
            Console.WriteLine("HomeHarbor CA SHA-256: " + fingerprint);
            return 0;
        });
        return command;
    }
}
