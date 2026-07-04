using System.CommandLine;
using HomeHarbor.Tooling;

internal static partial class AgentProgram
{
    private static Command CreateSuperRemoveCommand(ICommandRunner runner)
    {
        var mapperNameArgument = RequiredArgument("mapper-name", "Mapper name.");
        var command = new Command("remove", "Remove a logical partition mapping.")
        {
            Arguments = { mapperNameArgument }
        };
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            await new SuperMapper(runner).RemoveAsync(parseResult.GetValue(mapperNameArgument)!, cancellationToken);
            return 0;
        });
        return command;
    }
}
