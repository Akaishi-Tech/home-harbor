using System.CommandLine;
using HomeHarbor.Tooling;

internal static partial class AgentProgram
{
    private static Command CreateSelinuxStoreSyncCommand(ICommandRunner runner)
        => SimpleCommand(
            "selinux-store-sync",
            (_, _) =>
            {
                SelinuxRuntimeReadiness.RequireEnforcingDefault();
                var result = SelinuxPolicyStoreSynchronizer.SynchronizeDefaultDetailed();
                Console.WriteLine(result.StoreReplaced
                    ? "synchronized the SELinux policy store from the immutable image seed"
                    : "SELinux policy store already matches the immutable image seed");
                return Task.FromResult(0);
            },
            runner);

    private static Command CreateSelinuxRelabelCommand(ICommandRunner runner)
    {
        var command = new Command("selinux-relabel");
        command.Subcommands.Add(SimpleCommand(
            "persistent",
            async (commandRunner, cancellationToken) =>
            {
                SelinuxRuntimeReadiness.RequireEnforcingDefault();
                var changed = await SelinuxRelabelCoordinator.RelabelPersistentDefaultAsync(
                    commandRunner,
                    cancellationToken);
                Console.WriteLine(changed
                    ? "relabelled persistent state for the current SELinux policy epoch"
                    : "persistent state already matches the current SELinux policy epoch");
                return 0;
            },
            runner));
        command.Subcommands.Add(SimpleCommand(
            "managed",
            async (commandRunner, cancellationToken) =>
            {
                SelinuxRuntimeReadiness.RequireEnforcingDefault();
                await SelinuxRelabelCoordinator.RelabelManagedPathsDefaultAsync(
                    commandRunner,
                    cancellationToken);
                Console.WriteLine("labelled fixed HomeHarbor runtime paths without recursive traversal");
                return 0;
            },
            runner));
        command.Subcommands.Add(SimpleCommand(
            "data",
            async (commandRunner, cancellationToken) =>
            {
                SelinuxRuntimeReadiness.RequireEnforcingDefault();
                if (!await IsHomeHarborDataMountAsync(commandRunner, cancellationToken))
                {
                    throw new InvalidOperationException(
                        "HomeHarbor data relabel requires a mounted btrfs, xfs, or zfs data filesystem");
                }

                var changed = await SelinuxRelabelCoordinator.RelabelDataDefaultAsync(
                    commandRunner,
                    cancellationToken);
                Console.WriteLine(changed
                    ? "relabelled the mounted data filesystem for the current SELinux policy epoch"
                    : "the mounted data filesystem already matches the current SELinux policy epoch");
                return 0;
            },
            runner));
        return command;
    }

    private static Command CreateSelinuxReadyCheckCommand(ICommandRunner runner)
        => SimpleCommand(
            "selinux-ready-check",
            (_, _) =>
            {
                SelinuxRelabelCoordinator.RequirePersistentCurrentDefault();
                SelinuxRuntimeReadiness.RequireDefault();
                Console.WriteLine("SELinux is enforcing and required HomeHarbor runtime directories are ready");
                return Task.FromResult(0);
            },
            runner);
}
