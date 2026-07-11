using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace HomeHarbor.Tooling;

public interface ICommandRunner
{
    Task<CommandResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        CommandRunOptions? options = null,
        CancellationToken cancellationToken = default);
}

public sealed class ProcessCommandRunner : ICommandRunner
{
    public async Task<CommandResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        CommandRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= CommandRunOptions.Default;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (options.Timeout is { } timeoutValue)
        {
            timeout.CancelAfter(timeoutValue);
        }

        var start = new ProcessStartInfo(fileName)
        {
            RedirectStandardError = true,
            RedirectStandardInput = options.StandardInput is not null,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            WorkingDirectory = options.WorkingDirectory ?? string.Empty
        };

        foreach (var (key, value) in options.Environment)
        {
            start.Environment[key] = value;
        }

        foreach (var arg in arguments)
        {
            start.ArgumentList.Add(arg);
        }

        try
        {
            using var process = Process.Start(start)
                ?? throw new InvalidOperationException("Failed to start " + fileName);
            using var cancellationRegistration = timeout.Token.Register(
                static state =>
                {
                    var runningProcess = (Process)state!;
                    try
                    {
                        if (!runningProcess.HasExited)
                        {
                            runningProcess.Kill(entireProcessTree: true);
                        }
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
                    {
                        // The process exited concurrently or cannot be killed on this platform.
                    }
                },
                process);

            var stdout = ReadPipeAsync(process.StandardOutput, options.StreamOutput ? Console.Out : null, timeout.Token);
            var stderr = ReadPipeAsync(process.StandardError, options.StreamError ? Console.Error : null, timeout.Token);
            if (options.StandardInput is not null)
            {
                await process.StandardInput.WriteAsync(options.StandardInput.AsMemory(), timeout.Token);
                process.StandardInput.Close();
            }

            await process.WaitForExitAsync(timeout.Token);
            return new CommandResult(
                process.ExitCode,
                await stdout,
                await stderr,
                FormatCommand(fileName, arguments));
        }
        catch (Exception ex) when (ex is Win32Exception or OperationCanceledException or IOException)
        {
            if (options.ThrowOnStartFailure)
            {
                throw;
            }

            return new CommandResult(127, string.Empty, ex.Message, FormatCommand(fileName, arguments));
        }
    }

    private static async Task<string> ReadPipeAsync(
        TextReader reader,
        TextWriter? mirror,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        var buffer = new char[4096];
        while (true)
        {
            var read = await reader.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            _ = builder.Append(buffer, 0, read);
            if (mirror is not null)
            {
                await mirror.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
        }

        return builder.ToString();
    }

    public static string FormatCommand(string fileName, IEnumerable<string> arguments)
        => string.Join(' ', new[] { fileName }.Concat(arguments).Select(ShellQuote));

    private static string ShellQuote(string value)
        => value.Length > 0 && value.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' or '/' or ':' or '=' or '@')
            ? value
            : "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
}

public sealed record CommandRunOptions(
    string? WorkingDirectory = null,
    string? StandardInput = null,
    TimeSpan? Timeout = null,
    bool StreamOutput = false,
    bool StreamError = false,
    bool ThrowOnStartFailure = false,
    IReadOnlyDictionary<string, string>? EnvironmentOverride = null)
{
    public static CommandRunOptions Default { get; } = new();

    public IReadOnlyDictionary<string, string> Environment => EnvironmentOverride ?? new Dictionary<string, string>();
}

public sealed record CommandResult(int ExitCode, string Stdout, string Stderr, string Command)
{
    public string CombinedOutput => string.IsNullOrEmpty(Stdout) ? Stderr : string.IsNullOrEmpty(Stderr) ? Stdout : Stdout + Stderr;

    public CommandResult EnsureSuccess(string? message = null)
    {
        return ExitCode == 0
            ? this
            : throw new InvalidOperationException(
            (message ?? "Command failed") +
            $": {Command} exited {ExitCode}" +
            (string.IsNullOrWhiteSpace(CombinedOutput) ? string.Empty : Environment.NewLine + CombinedOutput.Trim()));
    }
}
