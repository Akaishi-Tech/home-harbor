using System.Diagnostics;
using System.Text;
using HomeHarbor.Tooling;

internal sealed record KernelAddon(string Key, string File, string Sha256);

internal sealed class InstallDiskIo(Action<string> write, TextReader input, bool inputInteractive)
{
    public bool InputInteractive { get; } = inputInteractive;

    public void Write(string text)
        => write(text);

    public void WriteLine(string text)
        => write(text + Environment.NewLine);

    public Task<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(input.ReadLine());
    }
}

internal static class InstallDiskProcess
{
    public static async Task<int> RunStreamingAsync(
        string fileName,
        IReadOnlyList<string> args,
        InstallDiskIo io,
        CancellationToken cancellationToken)
    {
        var result = await RunAsync(fileName, args, stream: true, io, cancellationToken);
        return result.ExitCode;
    }

    public static async Task RunRequiredAsync(
        string fileName,
        IReadOnlyList<string> args,
        InstallDiskIo io,
        string? message = null,
        bool stream = true,
        CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(fileName, args, stream, io, cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                (message ?? "Command failed") +
                $": {ProcessCommandRunner.FormatCommand(fileName, args)} exited {result.ExitCode}" +
                (string.IsNullOrWhiteSpace(result.Output) ? string.Empty : Environment.NewLine + result.Output.Trim()));
        }
    }

    public static async Task<string> CaptureRequiredAsync(
        string fileName,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var result = await CaptureAsync(fileName, args, cancellationToken);
        return result.ExitCode != 0
            ? throw new InvalidOperationException(
                $"Command failed: {ProcessCommandRunner.FormatCommand(fileName, args)} exited {result.ExitCode}" +
                (string.IsNullOrWhiteSpace(result.Output) ? string.Empty : Environment.NewLine + result.Output.Trim()))
            : result.Output;
    }

    public static Task<InstallDiskProcessResult> CaptureAsync(
        string fileName,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
        => RunAsync(fileName, args, stream: false, io: null, cancellationToken);

    private static async Task<InstallDiskProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> args,
        bool stream,
        InstallDiskIo? io,
        CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(fileName)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        try
        {
            using var process = Process.Start(start)
                ?? throw new InvalidOperationException("Failed to start " + fileName);
            var stdout = ReadPipeAsync(process.StandardOutput, stream ? io : null, cancellationToken);
            var stderr = ReadPipeAsync(process.StandardError, stream ? io : null, cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            _ = await Task.WhenAll(stdout, stderr);
            return new InstallDiskProcessResult(process.ExitCode, (await stdout) + (await stderr));
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
        {
            return new InstallDiskProcessResult(127, ex.Message);
        }
    }

    private static async Task<string> ReadPipeAsync(TextReader reader, InstallDiskIo? io, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        var buffer = new char[4096];
        while (true)
        {
            var read = await reader.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                return builder.ToString();
            }

            var chunk = new string(buffer, 0, read);
            _ = builder.Append(chunk);
            io?.Write(chunk);
        }
    }
}

internal sealed record InstallDiskProcessResult(int ExitCode, string Output);

internal static class InstallDiskStringExtensions
{
    public static bool EndsWithNumber(this string value)
        => value.Length > 0 && char.IsDigit(value[^1]);
}

internal static class UnixFileModes
{
    public const UnixFileMode Mode640 = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead;
    public const UnixFileMode Mode644 = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
    public const UnixFileMode Mode750 = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute;
    public const UnixFileMode Mode755 = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
}
