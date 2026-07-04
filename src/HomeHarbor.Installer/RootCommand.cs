using System.CommandLine;

internal static partial class InstallerProgram
{
    private static RootCommand CreateRootCommand()
    {
        var installerOptions = InstallerOptions.CreateCommandOptions();
        var tuiOption = new Option<bool>("--tui")
        {
            Description = "Compatibility flag; the installer UI remains automatic."
        };
        var root = new RootCommand("HomeHarbor installer.");
        installerOptions.AddTo(root);
        root.Options.Add(tuiOption);
        root.SetAction(parseResult => Installer.RunAsync(InstallerOptions.FromParseResult(parseResult, installerOptions)));
        root.Subcommands.Add(CreateVerifyOtaManifestCommand());
        root.Subcommands.Add(CreateBootStateCommand());
        root.Subcommands.Add(InstallDiskCommand.CreateCommand());
        return root;
    }
}
