using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace HomeHarbor.Api.Auth;

public static class JwtSigningKeyStore
{
    private static readonly ConcurrentDictionary<string, byte[]> CachedKeys = new(StringComparer.Ordinal);

    public static SymmetricSecurityKey GetOrCreateSecurityKey(string path)
        => new(GetOrCreateKey(path));

    public static byte[] GetOrCreateKey(string path)
    {
        return string.IsNullOrWhiteSpace(path)
            ? throw new InvalidOperationException("HomeHarbor:Jwt:SigningKeyPath is required.")
            : CachedKeys.GetOrAdd(Path.GetFullPath(path), static fullPath =>
        {
            if (File.Exists(fullPath))
                return ReadExistingKey(fullPath);

            var key = RandomNumberGenerator.GetBytes(64);
            _ = Directory.CreateDirectory(Path.GetDirectoryName(fullPath) ?? ".");
            try
            {
                using var stream = new FileStream(fullPath, new FileStreamOptions
                {
                    Access = FileAccess.Write,
                    Mode = FileMode.CreateNew,
                    Share = FileShare.None,
                    UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
                });
                var encoded = Encoding.ASCII.GetBytes(Convert.ToBase64String(key));
                stream.Write(encoded);
                stream.Flush(flushToDisk: true);
            }
            catch (IOException) when (File.Exists(fullPath))
            {
                return ReadExistingKey(fullPath);
            }

            return key;
        });
    }

    private static byte[] ReadExistingKey(string path)
    {
        var encoded = File.ReadAllText(path).Trim();
        byte[] existing;
        try
        {
            existing = Convert.FromBase64String(encoded);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException(
                $"The JWT signing key at '{path}' must be base64-encoded. Refusing to replace the existing key.",
                ex);
        }

        if (existing.Length < 32)
        {
            throw new InvalidOperationException(
                $"The JWT signing key at '{path}' must decode to at least 32 bytes. Refusing to replace the existing key.");
        }

        TryRestrictPermissions(path);
        return existing;
    }

    private static void TryRestrictPermissions(string path)
    {
        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (PlatformNotSupportedException)
        {
        }
    }
}
