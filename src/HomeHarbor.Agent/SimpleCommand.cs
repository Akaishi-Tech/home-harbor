using System.CommandLine;
using HomeHarbor.Tooling;

internal static partial class AgentProgram
{
    private static Command SimpleCommand(
        string name,
        Func<ICommandRunner, CancellationToken, Task<int>> action,
        ICommandRunner runner)
    {
        var command = new Command(name);
        command.SetAction((_, cancellationToken) => action(runner, cancellationToken));
        return command;
    }
}
