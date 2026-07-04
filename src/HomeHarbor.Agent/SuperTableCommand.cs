using System.CommandLine;
using HomeHarbor.Tooling;

internal static partial class AgentProgram
{
    private static Command CreateSuperTableCommand(ICommandRunner runner)
    {
        var superDeviceArgument = RequiredArgument("super-device", "Super partition device path.");
        var logicalPartitionArgument = RequiredArgument("logical-partition", "Logical partition name.");
        var command = new Command("table", "Print a logical partition dm table.")
        {
            Arguments = { superDeviceArgument, logicalPartitionArgument }
        };
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            Console.Write(await new SuperMapper(runner).TableAsync(
                parseResult.GetValue(superDeviceArgument)!,
                parseResult.GetValue(logicalPartitionArgument)!,
                cancellationToken));
            return 0;
        });
        return command;
    }
}
