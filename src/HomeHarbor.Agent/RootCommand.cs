using System.CommandLine;
using HomeHarbor.Tooling;

internal static partial class AgentProgram
{
    private static RootCommand CreateRootCommand(ICommandRunner runner)
    {
        var root = new RootCommand("HomeHarbor appliance agent commands.");
        root.SetAction(_ =>
        {
            PrintHelp(Console.Out);
            return 0;
        });

        root.Subcommands.Add(CreateFirstbootCommand(runner));
        root.Subcommands.Add(CreateConsumeSetupBootstrapCommand(runner));
        root.Subcommands.Add(CreatePostgresInitCommand(runner));
        root.Subcommands.Add(CreatePostgresBootstrapCommand(runner));
        root.Subcommands.Add(CreateEnsureCaddyConfigCommand(runner));
        root.Subcommands.Add(CreateDisplayTlsTrustCommand());
        root.Subcommands.Add(CreateRenderCaddyfileCommand(runner));
        root.Subcommands.Add(CreateStorageHealthCommand(runner));
        root.Subcommands.Add(CreateEnsureSmbConfigCommand(runner));
        root.Subcommands.Add(CreateApplySmbCommand(runner));
        root.Subcommands.Add(CreateApplyContainersCommand(runner));
        root.Subcommands.Add(CreateApplySystemAppsCommand(runner));
        root.Subcommands.Add(CreateBootAttemptCommand(runner));
        root.Subcommands.Add(CreateBootSuccessCommand(runner));
        root.Subcommands.Add(OtaApplyCommand.CreateCommand(runner));
        root.Subcommands.Add(CreateOtaCommitCommand(runner));
        root.Subcommands.Add(CreateStorageApplyCommand(runner));
        root.Subcommands.Add(CreateStoragePostApplyCommand(runner));
        root.Subcommands.Add(CreateSelinuxStoreSyncCommand(runner));
        root.Subcommands.Add(CreateSelinuxRelabelCommand(runner));
        root.Subcommands.Add(CreateSelinuxReadyCheckCommand(runner));
        root.Subcommands.Add(CreateBootStateCommand(runner));
        root.Subcommands.Add(CreateVerifyOtaManifestCommand(runner));
        root.Subcommands.Add(CreateSuperCommand(runner));
        return root;
    }
}
