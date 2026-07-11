using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace HomeHarbor.Api.Services;

public sealed class RuntimeSignalService(IOptions<HomeHarborRuntimeOptions> options) : IRuntimeSignalService
{
    private readonly HomeHarborRuntimeOptions _options = options.Value;

    public void RequestSmbApply()
        => Touch(Path.Combine(_options.RequestDirectory, "smb-apply.request"));

    public void RequestContainerApply()
        => Touch(Path.Combine(_options.RequestDirectory, "container-apply.request"));

    public void RequestSystemAppApply()
        => Touch(Path.Combine(_options.RequestDirectory, "system-app-apply.request"));

    public void RequestCaddyRender()
        => Touch(Path.Combine(_options.RequestDirectory, "caddy-render.request"));

    public async Task WriteSmbPasswordAsync(
        Guid credentialId,
        string username,
        string unixUser,
        string password,
        CancellationToken cancellationToken)
    {
        _ = Directory.CreateDirectory(_options.SmbCredentialDirectory);
        var path = Path.Combine(_options.SmbCredentialDirectory, $"{credentialId:N}.json");
        var payload = JsonSerializer.Serialize(new
        {
            action = "upsert",
            credentialId,
            username,
            unixUser,
            password
        });
        await WriteAtomicAsync(path, payload, cancellationToken);
    }

    public async Task WriteSmbRevokeAsync(
        Guid credentialId,
        string username,
        string unixUser,
        CancellationToken cancellationToken)
    {
        _ = Directory.CreateDirectory(_options.SmbCredentialDirectory);
        var path = Path.Combine(_options.SmbCredentialDirectory, $"{credentialId:N}.json");
        var payload = JsonSerializer.Serialize(new
        {
            action = "revoke",
            credentialId,
            username,
            unixUser
        });
        await WriteAtomicAsync(path, payload, cancellationToken);
    }

    private static void Touch(string path)
    {
        var payload = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        WriteAtomic(path, Encoding.UTF8.GetBytes(payload));
    }

    private static async Task WriteAtomicAsync(
        string path,
        string payload,
        CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(payload);
        var temporaryPath = PrepareTemporaryPath(path);
        try
        {
            await using (var stream = OpenSecureTemporaryFile(temporaryPath))
            {
                await stream.WriteAsync(bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            EnforceOwnerOnlyMode(temporaryPath);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private static void WriteAtomic(string path, ReadOnlySpan<byte> payload)
    {
        var temporaryPath = PrepareTemporaryPath(path);
        try
        {
            using (var stream = OpenSecureTemporaryFile(temporaryPath))
            {
                stream.Write(payload);
                stream.Flush(flushToDisk: true);
            }

            EnforceOwnerOnlyMode(temporaryPath);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private static string PrepareTemporaryPath(string path)
    {
        var directory = Path.GetDirectoryName(path) ?? ".";
        _ = Directory.CreateDirectory(directory);
        return Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
    }

    private static FileStream OpenSecureTemporaryFile(string path)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            BufferSize = 4096,
            Options = FileOptions.WriteThrough
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        return new FileStream(path, options);
    }

    private static void EnforceOwnerOnlyMode(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Best-effort cleanup. CreateNew and a random name make stale files harmless.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup; retain the original write failure.
        }
    }
}
