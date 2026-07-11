using System.Security.Cryptography;
using System.Text;
using HomeHarbor.Tooling;

internal static class SetupBootstrapCode
{
    internal const string DefaultCodePath = "/var/lib/homeharbor/setup/bootstrap-code";
    internal const string DefaultCompletePath = "/var/lib/homeharbor/setup/bootstrap-complete";
    internal const string DefaultConsumeRequestPath = "/run/homeharbor/setup-bootstrap-consume";
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public static async Task<string?> EnsureAndDisplayAsync(
        ICommandRunner runner,
        string codePath,
        string completePath,
        IReadOnlyList<string> consoles,
        CancellationToken cancellationToken)
    {
        if (PathExists(completePath))
        {
            RequireRegularFile(completePath, "setup bootstrap completion marker");
            return null;
        }

        _ = RootPathGuard.CreateDirectory(Path.GetDirectoryName(codePath) ?? ".", "setup bootstrap directory");
        _ = RootPathGuard.RequireNoSymlinkComponents(codePath, "setup bootstrap code path");
        string code;
        if (PathExists(codePath))
        {
            RequireRegularFile(codePath, "setup bootstrap code");
            code = (await File.ReadAllTextAsync(codePath, cancellationToken)).Trim();
            if (!IsValid(code))
            {
                throw new InvalidOperationException("existing setup bootstrap code has an invalid format");
            }
        }
        else
        {
            code = Generate();
            await CreateNewSecretFileAsync(codePath, code + Environment.NewLine, cancellationToken);
        }

        File.SetUnixFileMode(codePath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        _ = (await runner.RunAsync("chown", ["root:homeharbor", codePath], cancellationToken: cancellationToken))
            .EnsureSuccess("failed to protect setup bootstrap code ownership");
        await DisplayOnPhysicalConsolesAsync(code, consoles, cancellationToken);
        return code;
    }

    public static async Task ConsumeAsync(
        ICommandRunner runner,
        string requestPath,
        string codePath,
        string completePath,
        CancellationToken cancellationToken)
    {
        _ = RootPathGuard.RequireNoSymlinkComponents(requestPath, "setup bootstrap consume request path");
        _ = RootPathGuard.RequireNoSymlinkComponents(codePath, "setup bootstrap code path");
        RequireRegularFile(requestPath, "setup bootstrap consume request");
        var request = (await File.ReadAllTextAsync(requestPath, cancellationToken)).Trim();
        if (!string.Equals(request, "consume", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("setup bootstrap consume request is invalid");
        }

        if (PathExists(codePath))
        {
            RequireRegularFile(codePath, "setup bootstrap code");
            File.Delete(codePath);
        }

        _ = RootPathGuard.CreateDirectory(Path.GetDirectoryName(completePath) ?? ".", "setup bootstrap directory");
        _ = RootPathGuard.RequireNoSymlinkComponents(completePath, "setup bootstrap completion path");
        if (!PathExists(completePath))
        {
            await CreateNewSecretFileAsync(
                completePath,
                DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture) + Environment.NewLine,
                cancellationToken);
        }
        else
        {
            RequireRegularFile(completePath, "setup bootstrap completion marker");
        }

        File.SetUnixFileMode(completePath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        _ = (await runner.RunAsync("chown", ["root:homeharbor", completePath], cancellationToken: cancellationToken))
            .EnsureSuccess("failed to protect setup bootstrap completion marker ownership");
        File.Delete(requestPath);
    }

    internal static string Generate()
    {
        Span<char> characters = stackalloc char[19];
        for (var group = 0; group < 4; group++)
        {
            if (group > 0)
            {
                characters[group * 5 - 1] = '-';
            }

            for (var index = 0; index < 4; index++)
            {
                characters[group * 5 + index] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
            }
        }

        return new string(characters);
    }

    internal static bool IsValid(string value)
    {
        if (value.Length != 19)
        {
            return false;
        }

        for (var index = 0; index < value.Length; index++)
        {
            if (index is 4 or 9 or 14)
            {
                if (value[index] != '-')
                {
                    return false;
                }
            }
            else if (!Alphabet.Contains(value[index], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static async Task CreateNewSecretFileAsync(string path, string contents, CancellationToken cancellationToken)
    {
        var options = new FileStreamOptions
        {
            Access = FileAccess.Write,
            Mode = FileMode.CreateNew,
            Share = FileShare.None,
            Options = FileOptions.WriteThrough,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead
        };
        await using var stream = new FileStream(path, options);
        var bytes = Encoding.UTF8.GetBytes(contents);
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
        stream.Flush(flushToDisk: true);
    }

    private static async Task DisplayOnPhysicalConsolesAsync(
        string code,
        IReadOnlyList<string> consoles,
        CancellationToken cancellationToken)
    {
        var message = Environment.NewLine +
            "HomeHarbor first-run setup code" + Environment.NewLine +
            code + Environment.NewLine +
            "Enter this code in the HomeHarbor setup screen." + Environment.NewLine + Environment.NewLine;
        var displayed = 0;
        var failures = new List<string>();
        foreach (var console in consoles.Distinct(StringComparer.Ordinal))
        {
            try
            {
                await using var stream = new FileStream(console, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
                var bytes = Encoding.UTF8.GetBytes(message);
                await stream.WriteAsync(bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                displayed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileNotFoundException or DirectoryNotFoundException)
            {
                failures.Add(console + ": " + ex.Message);
            }
        }

        if (displayed == 0)
        {
            throw new InvalidOperationException(
                "setup bootstrap code could not be displayed on a physical console" +
                (failures.Count == 0 ? string.Empty : ": " + string.Join("; ", failures)));
        }
    }

    private static bool PathExists(string path)
        => File.Exists(path) || Directory.Exists(path) || new FileInfo(path).LinkTarget is not null || new DirectoryInfo(path).LinkTarget is not null;

    private static void RequireRegularFile(string path, string label)
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new InvalidOperationException(label + " is missing: " + path, ex);
        }

        if (attributes.HasFlag(FileAttributes.Directory) || attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidOperationException(label + " must be a regular file: " + path);
        }
    }
}
