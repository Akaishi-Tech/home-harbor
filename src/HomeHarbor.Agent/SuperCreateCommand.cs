using System.CommandLine;
using HomeHarbor.Tooling;

internal static partial class AgentProgram
{
    private static Command CreateSuperCreateCommand(ICommandRunner runner)
    {
        var mapperNameArgument = RequiredArgument("mapper-name", "Mapper name.");
        var superDeviceArgument = RequiredArgument("super-device", "Super partition device path.");
        var logicalPartitionArgument = RequiredArgument("logical-partition", "Logical partition name.");
        var modeArgument = OptionalArgument("mode", "rw", "Mapping mode.");
        var command = new Command("create", "Create a logical partition mapping.")
        {
            Arguments = { mapperNameArgument, superDeviceArgument, logicalPartitionArgument, modeArgument }
        };
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            await new SuperMapper(runner).CreateAsync(
                parseResult.GetValue(mapperNameArgument)!,
                parseResult.GetValue(superDeviceArgument)!,
                parseResult.GetValue(logicalPartitionArgument)!,
                parseResult.GetValue(modeArgument)!,
                cancellationToken);
            return 0;
        });
        return command;
    }
}
