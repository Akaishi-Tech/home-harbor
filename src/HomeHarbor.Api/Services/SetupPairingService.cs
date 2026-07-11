
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace HomeHarbor.Api.Services;

public sealed class SetupPairingService(
    ITokenGenerator tokens,
    IOptions<SetupPairingOptions> options,
    ILogger<SetupPairingService> logger) : ISetupPairingService
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);
    private readonly Lock _gate = new();
    private SetupPairingTicket? _current;
    private bool _bootstrapConsumed;

    public bool IsBootstrapComplete()
        => File.Exists(options.Value.BootstrapCompletePath);

    public SetupPairingTicket GetOrCreate(string publicOrigin, Guid familyId)
    {
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            if (_current is not null && _current.FamilyId == familyId && _current.ExpiresAt > now.AddMinutes(1))
                return _current with { PublicOrigin = publicOrigin, PairingUrl = BuildPairingUrl(publicOrigin, _current.Code) };

            var code = tokens.GenerateRecoveryCode();
            _current = new SetupPairingTicket(
                code,
                familyId,
                publicOrigin,
                BuildPairingUrl(publicOrigin, code),
                now.Add(Lifetime));
            return _current;
        }
    }

    public bool IsBootstrapCodeValid(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return false;

        string expected;
        lock (_gate)
        {
            if (_bootstrapConsumed) return false;
            try
            {
                expected = File.ReadAllText(options.Value.BootstrapCodePath).Trim();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
            return FixedTimeCodeEquals(expected, code);
        }
    }

    public bool IsDeviceCodeValid(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return false;

        lock (_gate)
        {
            return _current is not null &&
                _current.ExpiresAt > DateTimeOffset.UtcNow &&
                FixedTimeCodeEquals(_current.Code, code);
        }
    }

    public bool TryConsumeDeviceCode(string? code, out SetupPairingTicket? ticket)
    {
        ticket = null;
        if (string.IsNullOrWhiteSpace(code)) return false;

        lock (_gate)
        {
            if (_current is not null &&
                _current.ExpiresAt > DateTimeOffset.UtcNow &&
                FixedTimeCodeEquals(_current.Code, code))
            {
                ticket = _current;
                _current = null;
                return true;
            }

            return false;
        }
    }

    public void ConsumeBootstrapCode(string? code)
    {
        lock (_gate)
        {
            if (_bootstrapConsumed || !BootstrapCodeMatchesFile(code)) return;
            _bootstrapConsumed = true;
        }

        try
        {
            WriteConsumeRequest(options.Value.ConsumeRequestPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Failed to request deletion of the consumed setup bootstrap code.");
        }
    }

    private static string BuildPairingUrl(string publicOrigin, string code)
        => $"{publicOrigin.TrimEnd('/')}/pair#code={Uri.EscapeDataString(code)}";

    private static bool FixedTimeCodeEquals(string expected, string supplied)
    {
        var expectedBytes = Encoding.UTF8.GetBytes(expected.Trim().ToUpperInvariant());
        var suppliedBytes = Encoding.UTF8.GetBytes(supplied.Trim().ToUpperInvariant());
        return expectedBytes.Length == suppliedBytes.Length &&
            CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes);
    }

    private bool BootstrapCodeMatchesFile(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return false;
        try
        {
            return FixedTimeCodeEquals(File.ReadAllText(options.Value.BootstrapCodePath), code);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void WriteConsumeRequest(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException("HomeHarbor:Setup:ConsumeRequestPath is required.");

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath) ?? ".";
        _ = Directory.CreateDirectory(directory);
        var tempPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var output = new FileStream(tempPath, new FileStreamOptions
            {
                Access = FileAccess.Write,
                Mode = FileMode.CreateNew,
                Share = FileShare.None,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
            }))
            using (var writer = new StreamWriter(output, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                writer.WriteLine("consume");
                writer.Flush();
                output.Flush(flushToDisk: true);
            }

            File.Move(tempPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }
}

public sealed record SetupPairingTicket(
    string Code,
    Guid FamilyId,
    string PublicOrigin,
    string PairingUrl,
    DateTimeOffset ExpiresAt);
