using System.Globalization;
using System.Security.Cryptography;
using System.Text;

internal sealed record FastbootUnlockGrant(DateTimeOffset ExpiresAt, string AuthorizationToken);

internal sealed class FastbootSessionAuthorization(byte[] secret) : IDisposable
{
    private byte[]? _secret = secret;

    internal ReadOnlySpan<byte> Secret => _secret ?? [];

    public void Dispose()
    {
        var secret = Interlocked.Exchange(ref _secret, null);
        if (secret is not null)
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }
}

internal sealed class FastbootUnlockGate(string path, TimeProvider? timeProvider = null)
{
    public const string DefaultPath = "/run/homeharbor-recovery/fastboot-unlocked";
    public static readonly TimeSpan DefaultDuration = TimeSpan.FromMinutes(10);
    private const string StateVersion = "HHFB1";
    private const int SecretBytes = 32;
    private const int SecretHexLength = SecretBytes * 2;
    private const int MaximumStateBytes = 256;
    private static readonly TimeSpan MaximumDuration = TimeSpan.FromHours(1);
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public string Path { get; } = System.IO.Path.GetFullPath(path);

    private string LockPath => Path + ".lock";

    public static FastbootUnlockGate FromEnvironment()
        => new(Environment.GetEnvironmentVariable("HOMEHARBOR_FASTBOOTD_UNLOCK_FILE") ?? DefaultPath);

    public FastbootUnlockGrant Grant(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero || duration > MaximumDuration)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), "fastboot unlock duration must be between one second and one hour");
        }

        var expiresAt = _timeProvider.GetUtcNow().Add(duration);
        var tokenBytes = RandomNumberGenerator.GetBytes(SecretBytes);
        var tokenHash = SHA256.HashData(tokenBytes);
        try
        {
            using var stateLock = AcquireStateLock();
            WriteState(new UnlockState(expiresAt.ToUnixTimeSeconds(), tokenHash, null));
            return new FastbootUnlockGrant(expiresAt, Convert.ToHexString(tokenBytes).ToLowerInvariant());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(tokenBytes);
            CryptographicOperations.ZeroMemory(tokenHash);
        }
    }

    public bool IsUnlocked(out TimeSpan remaining)
    {
        remaining = TimeSpan.Zero;
        if (!File.Exists(Path))
        {
            return false;
        }

        try
        {
            using var stateLock = AcquireStateLock();
            return TryReadCurrentState(deleteInvalid: true, out _, out remaining);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException)
        {
            remaining = TimeSpan.Zero;
            return false;
        }
    }

    public bool TryAuthorizeSession(ReadOnlySpan<byte> authorizationToken, out FastbootSessionAuthorization? authorization)
    {
        authorization = null;
        Span<byte> tokenBytes = stackalloc byte[SecretBytes];
        Span<byte> suppliedHash = stackalloc byte[SecretBytes];
        if (!TryDecodeToken(authorizationToken, tokenBytes))
        {
            return false;
        }

        try
        {
            _ = SHA256.HashData(tokenBytes, suppliedHash);
            using var stateLock = AcquireStateLock();
            if (!TryReadCurrentState(deleteInvalid: true, out var state, out _) ||
                state.TokenHash is null ||
                state.SessionHash is not null ||
                !CryptographicOperations.FixedTimeEquals(suppliedHash, state.TokenHash))
            {
                return false;
            }

            var sessionSecret = RandomNumberGenerator.GetBytes(SecretBytes);
            var sessionHash = SHA256.HashData(sessionSecret);
            try
            {
                WriteState(new UnlockState(state.ExpiresUnix, null, sessionHash));
                authorization = new FastbootSessionAuthorization(sessionSecret);
                sessionSecret = [];
                return true;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(sessionSecret);
                CryptographicOperations.ZeroMemory(sessionHash);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException)
        {
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(tokenBytes);
            CryptographicOperations.ZeroMemory(suppliedHash);
        }
    }

    public bool IsSessionAuthorized(FastbootSessionAuthorization authorization, out TimeSpan remaining)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        remaining = TimeSpan.Zero;
        if (authorization.Secret.Length != SecretBytes || !File.Exists(Path))
        {
            return false;
        }

        Span<byte> sessionHash = stackalloc byte[SecretBytes];
        try
        {
            _ = SHA256.HashData(authorization.Secret, sessionHash);
            using var stateLock = AcquireStateLock();
            return TryReadCurrentState(deleteInvalid: true, out var state, out remaining) &&
                state.TokenHash is null &&
                state.SessionHash is not null &&
                CryptographicOperations.FixedTimeEquals(sessionHash, state.SessionHash);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException)
        {
            remaining = TimeSpan.Zero;
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(sessionHash);
        }
    }

    public void EndSession(FastbootSessionAuthorization authorization)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        if (authorization.Secret.Length != SecretBytes || !File.Exists(Path))
        {
            return;
        }

        Span<byte> sessionHash = stackalloc byte[SecretBytes];
        try
        {
            _ = SHA256.HashData(authorization.Secret, sessionHash);
            using var stateLock = AcquireStateLock();
            if (TryReadCurrentState(deleteInvalid: true, out var state, out _) &&
                state.SessionHash is not null &&
                CryptographicOperations.FixedTimeEquals(sessionHash, state.SessionHash))
            {
                DeleteStateFile();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException)
        {
            // The one-time token remains consumed, so losing cleanup cannot authorize another session.
        }
        finally
        {
            CryptographicOperations.ZeroMemory(sessionHash);
        }
    }

    public void Revoke()
    {
        if (!File.Exists(Path))
        {
            return;
        }

        using var stateLock = AcquireStateLock();
        DeleteStateFile();
    }

    private FileStream AcquireStateLock()
    {
        var directory = System.IO.Path.GetDirectoryName(Path) ?? ".";
        _ = Directory.CreateDirectory(directory);
        var stateLock = new FileStream(LockPath, new FileStreamOptions
        {
            Access = FileAccess.ReadWrite,
            Mode = FileMode.OpenOrCreate,
            Share = FileShare.ReadWrite,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
        });
        try
        {
            File.SetUnixFileMode(LockPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            stateLock.Lock(0, 1);
            return stateLock;
        }
        catch
        {
            stateLock.Dispose();
            throw;
        }
    }

    private bool TryReadCurrentState(bool deleteInvalid, out UnlockState state, out TimeSpan remaining)
    {
        state = default;
        remaining = TimeSpan.Zero;
        if (!TryReadState(out state))
        {
            if (deleteInvalid)
            {
                DeleteStateFile();
            }

            return false;
        }

        var expiresAt = DateTimeOffset.FromUnixTimeSeconds(state.ExpiresUnix);
        remaining = expiresAt - _timeProvider.GetUtcNow();
        if (remaining <= TimeSpan.Zero || remaining > MaximumDuration)
        {
            remaining = TimeSpan.Zero;
            if (deleteInvalid)
            {
                DeleteStateFile();
            }

            return false;
        }

        return true;
    }

    private bool TryReadState(out UnlockState state)
    {
        state = default;
        if (!File.Exists(Path))
        {
            return false;
        }

        var attributes = File.GetAttributes(Path);
        var fileInfo = new FileInfo(Path);
        if (attributes.HasFlag(FileAttributes.Directory) ||
            attributes.HasFlag(FileAttributes.ReparsePoint) ||
            fileInfo.Length <= 0 ||
            fileInfo.Length > MaximumStateBytes)
        {
            return false;
        }

        var lines = File.ReadAllLines(Path, Encoding.UTF8);
        if (lines.Length != 4 ||
            !string.Equals(lines[0], StateVersion, StringComparison.Ordinal) ||
            !long.TryParse(lines[1], NumberStyles.None, CultureInfo.InvariantCulture, out var expiresUnix) ||
            !TryDecodeHash(lines[2], out var tokenHash) ||
            !TryDecodeHash(lines[3], out var sessionHash) ||
            (tokenHash is null) == (sessionHash is null))
        {
            return false;
        }

        state = new UnlockState(expiresUnix, tokenHash, sessionHash);
        return true;
    }

    private void WriteState(UnlockState state)
    {
        var directory = System.IO.Path.GetDirectoryName(Path) ?? ".";
        _ = Directory.CreateDirectory(directory);
        var temp = Path + ".tmp." + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + "." + Guid.NewGuid().ToString("N");
        try
        {
            var value = string.Join('\n',
                StateVersion,
                state.ExpiresUnix.ToString(CultureInfo.InvariantCulture),
                state.TokenHash is null ? "-" : Convert.ToHexString(state.TokenHash).ToLowerInvariant(),
                state.SessionHash is null ? "-" : Convert.ToHexString(state.SessionHash).ToLowerInvariant()) + "\n";
            File.WriteAllText(temp, value, new UTF8Encoding(false));
            File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temp, Path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    private void DeleteStateFile()
    {
        if (!File.Exists(Path))
        {
            return;
        }

        var attributes = File.GetAttributes(Path);
        if (!attributes.HasFlag(FileAttributes.Directory) && !attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            File.Delete(Path);
        }
    }

    private static bool TryDecodeToken(ReadOnlySpan<byte> value, Span<byte> destination)
    {
        if (value.Length != SecretHexLength || destination.Length != SecretBytes)
        {
            return false;
        }

        for (var index = 0; index < destination.Length; index++)
        {
            var high = HexValue(value[index * 2]);
            var low = HexValue(value[(index * 2) + 1]);
            if (high < 0 || low < 0)
            {
                CryptographicOperations.ZeroMemory(destination);
                return false;
            }

            destination[index] = (byte)((high << 4) | low);
        }

        return true;
    }

    private static bool TryDecodeHash(string value, out byte[]? hash)
    {
        hash = null;
        if (string.Equals(value, "-", StringComparison.Ordinal))
        {
            return true;
        }

        if (value.Length != SecretHexLength)
        {
            return false;
        }

        var buffer = new byte[SecretBytes];
        if (!TryDecodeToken(Encoding.ASCII.GetBytes(value), buffer))
        {
            CryptographicOperations.ZeroMemory(buffer);
            return false;
        }

        hash = buffer;
        return true;
    }

    private static int HexValue(byte value) => value switch
    {
        >= (byte)'0' and <= (byte)'9' => value - (byte)'0',
        >= (byte)'a' and <= (byte)'f' => value - (byte)'a' + 10,
        >= (byte)'A' and <= (byte)'F' => value - (byte)'A' + 10,
        _ => -1
    };

    private readonly record struct UnlockState(long ExpiresUnix, byte[]? TokenHash, byte[]? SessionHash);
}
