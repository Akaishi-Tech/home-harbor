using System.Globalization;
using System.Text;

namespace HomeHarbor.Tooling;

public static class FileWrites
{
    public static async Task AtomicWriteTextAsync(
        string path,
        string contents,
        int? unixMode = null,
        CancellationToken cancellationToken = default)
    {
        _ = Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        var temp = path + ".tmp." + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + "." + Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(temp, contents, new UTF8Encoding(false), cancellationToken);
        if (unixMode is { } mode)
        {
            await ProcessModeAsync(temp, mode, cancellationToken);
        }

        File.Move(temp, path, overwrite: true);
    }

    public static void AtomicWriteText(string path, string contents, int? unixMode = null)
    {
        _ = Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        var temp = path + ".tmp." + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + "." + Guid.NewGuid().ToString("N");
        File.WriteAllText(temp, contents, new UTF8Encoding(false));
        if (unixMode is { } mode)
        {
            ProcessMode(temp, mode);
        }

        File.Move(temp, path, overwrite: true);
    }

    public static void EnsureDirectory(string path, int? unixMode = null)
    {
        _ = Directory.CreateDirectory(path);
        if (unixMode is { } mode)
        {
            ProcessMode(path, mode);
        }
    }

    public static async Task CopyFileAsync(
        string source,
        string destination,
        int? unixMode = null,
        CancellationToken cancellationToken = default)
    {
        _ = Directory.CreateDirectory(Path.GetDirectoryName(destination) ?? ".");
        await using var input = File.OpenRead(source);
        await using var output = File.Create(destination);
        await input.CopyToAsync(output, cancellationToken);
        if (unixMode is { } mode)
        {
            await ProcessModeAsync(destination, mode, cancellationToken);
        }
    }

    private static void ProcessMode(string path, int mode)
    {
        var result = new ProcessCommandRunner()
            .RunAsync("chmod", [mode.ToString("0000", CultureInfo.InvariantCulture), path])
            .GetAwaiter()
            .GetResult();
        _ = result.EnsureSuccess("chmod failed");
    }

    private static async Task ProcessModeAsync(string path, int mode, CancellationToken cancellationToken)
    {
        var result = await new ProcessCommandRunner()
            .RunAsync("chmod", [mode.ToString("0000", CultureInfo.InvariantCulture), path], cancellationToken: cancellationToken);
        _ = result.EnsureSuccess("chmod failed");
    }
}
