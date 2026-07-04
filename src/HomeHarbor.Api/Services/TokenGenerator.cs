using System.Security.Cryptography;

namespace HomeHarbor.Api.Services;

public sealed class TokenGenerator : ITokenGenerator
{
    private const string UsernameAlphabet = "abcdefghijklmnopqrstuvwxyz0123456789";

    public string GenerateUsername(string prefix = "hh")
        => $"{prefix}-{RandomNumberGenerator.GetString(UsernameAlphabet, 10)}";

    public string GenerateSecret(int byteLength = 32)
    {
        var bytes = RandomNumberGenerator.GetBytes(byteLength);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    public string GenerateRecoveryCode()
        => string.Join('-', Enumerable.Range(0, 4).Select(_ =>
            RandomNumberGenerator.GetString("ABCDEFGHJKLMNPQRSTUVWXYZ23456789", 4)));
}
