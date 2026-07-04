using HomeHarbor.Tooling;

namespace HomeHarbor.Tests;

[TestClass]
public sealed class CliCommandLineTests
{
    [TestMethod]
    public async Task Agent_CommandLine_Returns_Help_And_Parse_Error_Exit_Codes()
    {
        using var _ = new ConsoleCapture();
        var runner = new NullCommandRunner();

        Assert.AreEqual(0, await AgentProgram.RunAsync(["--help"], runner, CancellationToken.None));
        Assert.AreEqual(0, await AgentProgram.RunAsync([], runner, CancellationToken.None));
        Assert.AreEqual(2, await AgentProgram.RunAsync(["not-a-command"], runner, CancellationToken.None));
        Assert.AreEqual(2, await AgentProgram.RunAsync(["boot-attempt", "--threshold", "-1"], runner, CancellationToken.None));
    }

    [TestMethod]
    public async Task Installer_CommandLine_Returns_Help_And_Parse_Error_Exit_Codes()
    {
        using var _ = new ConsoleCapture();

        Assert.AreEqual(0, await InstallerProgram.RunAsync(["--help"], CancellationToken.None));
        Assert.AreEqual(0, await InstallerProgram.RunAsync(["install-disk", "--help"], CancellationToken.None));
        Assert.AreEqual(2, await InstallerProgram.RunAsync(["--not-real"], CancellationToken.None));
        Assert.AreEqual(2, await InstallerProgram.RunAsync(["install-disk", "--data-unlock"], CancellationToken.None));
    }

    [TestMethod]
    public async Task ImageBuilder_CommandLine_Parses_Help_Plan_And_Invalid_Channel()
    {
        using var _ = new ConsoleCapture();
        using var cwd = new CurrentDirectoryScope(RepositoryRoot());

        Assert.AreEqual(0, await ImageBuilderProgram.RunAsync(["--help"], CancellationToken.None));
        Assert.AreEqual(0, await ImageBuilderProgram.RunAsync(["plan", "0.1.0-dev"], CancellationToken.None));
        Assert.AreEqual(2, await ImageBuilderProgram.RunAsync(["not-a-command"], CancellationToken.None));
        Assert.AreEqual(2, await ImageBuilderProgram.RunAsync(["kernel-package-build", "/tmp/kernel", "0.1.0-dev", "bad"], CancellationToken.None));
    }

    [TestMethod]
    public async Task Recovery_CommandLine_Uses_Injected_Actions()
    {
        using var _ = new ConsoleCapture();

        Assert.AreEqual(0, await RecoveryProgram.RunAsync(
            ["--help"],
            _ => Task.FromResult(10),
            _ => Task.FromResult(20),
            CancellationToken.None));
        Assert.AreEqual(10, await RecoveryProgram.RunAsync(
            [],
            _ => Task.FromResult(10),
            _ => Task.FromResult(20),
            CancellationToken.None));
        Assert.AreEqual(20, await RecoveryProgram.RunAsync(
            ["--fastboot-tcp"],
            _ => Task.FromResult(10),
            _ => Task.FromResult(20),
            CancellationToken.None));
        Assert.AreEqual(2, await RecoveryProgram.RunAsync(
            ["--not-real"],
            _ => Task.FromResult(10),
            _ => Task.FromResult(20),
            CancellationToken.None));
    }

    private sealed class NullCommandRunner : ICommandRunner
    {
        public Task<CommandResult> RunAsync(
            string fileName,
            IEnumerable<string> arguments,
            CommandRunOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new CommandResult(0, string.Empty, string.Empty, fileName));
    }

    private sealed class ConsoleCapture : IDisposable
    {
        private readonly TextWriter _out = Console.Out;
        private readonly TextWriter _error = Console.Error;

        public ConsoleCapture()
        {
            Console.SetOut(TextWriter.Null);
            Console.SetError(TextWriter.Null);
        }

        public void Dispose()
        {
            Console.SetOut(_out);
            Console.SetError(_error);
        }
    }

    private sealed class CurrentDirectoryScope : IDisposable
    {
        private readonly string _original = Directory.GetCurrentDirectory();

        public CurrentDirectoryScope(string path)
        {
            Directory.SetCurrentDirectory(path);
        }

        public void Dispose()
            => Directory.SetCurrentDirectory(_original);
    }

    private static string RepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "HomeHarbor.slnx")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Repository root not found.");
    }
}
