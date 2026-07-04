using System.Diagnostics;
using System.Text;

namespace HomeHarbor.FullE2E.Tests.Infrastructure;

internal sealed class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string?>? environment = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory ?? RepoPaths.Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        if (environment is not null)
        {
            foreach (var (key, value) in environment)
            {
                if (value is null)
                {
                    _ = process.StartInfo.Environment.Remove(key);
                }
                else
                {
                    process.StartInfo.Environment[key] = value;
                }
            }
        }

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null) _ = stdout.AppendLine(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null) _ = stderr.AppendLine(e.Data);
        };

        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start process: {fileName}");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = timeout is null
            ? null
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts?.CancelAfter(timeout.GetValueOrDefault());

        try
        {
            await process.WaitForExitAsync(timeoutCts?.Token ?? cancellationToken);
            await process.WaitForExitAsync(CancellationToken.None);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            await TryWaitForExitAfterKillAsync(process);
            throw new TimeoutException(
                $"Process timed out after {timeout}: {FormatCommand(fileName, arguments)}\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            await TryWaitForExitAfterKillAsync(process);
            throw;
        }

        return new ProcessResult(
            process.ExitCode,
            stdout.ToString(),
            stderr.ToString(),
            FormatCommand(fileName, arguments));
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Performance",
        "CA1822:Mark members as static",
        Justification = "Kept as an instance method to match the E2E helper usage shape.")]
    public async Task<ProcessResult> RunRequiredAsync(
        string fileName,
        IEnumerable<string> arguments,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string?>? environment = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(fileName, arguments, workingDirectory, environment, timeout, cancellationToken);
        result.EnsureSuccess();
        return result;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }

    private static async Task TryWaitForExitAfterKillAsync(Process process)
    {
        try
        {
            using var waitCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await process.WaitForExitAsync(waitCts.Token);
        }
        catch (InvalidOperationException)
        {
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static string FormatCommand(string fileName, IEnumerable<string> arguments)
        => string.Join(' ', new[] { fileName }.Concat(arguments.Select(Quote)));

    private static string Quote(string value)
        => value.Any(char.IsWhiteSpace) || value.Contains('"', StringComparison.Ordinal)
            ? "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\""
            : value;
}

internal sealed record ProcessResult(int ExitCode, string Stdout, string Stderr, string Command)
{
    public void EnsureSuccess()
    {
        if (ExitCode == 0) return;

        throw new AssertFailedException(
            $"Command failed with exit code {ExitCode}: {Command}\nSTDOUT:\n{Summarize(Stdout)}\nSTDERR:\n{Summarize(Stderr)}");
    }

    private static string Summarize(string text)
    {
        const int limit = 12_000;
        if (text.Length <= limit) return text;

        var half = limit / 2;
        return text[..half] +
            $"\n... <truncated {text.Length - limit} chars> ...\n" +
            text[^half..];
    }
}
