namespace HomeHarbor.Api.Auth;

public static class LocalPasswordVerifier
{
    private static readonly string DummyHash = BCrypt.Net.BCrypt.HashPassword(
        "HomeHarbor dummy password used only for timing equalization");

    public static bool Verify(string? password, string? passwordHash)
    {
        var candidate = password ?? string.Empty;
        var hash = string.IsNullOrWhiteSpace(passwordHash) ? DummyHash : passwordHash;
        try
        {
            return BCrypt.Net.BCrypt.Verify(candidate, hash) && !string.IsNullOrWhiteSpace(passwordHash);
        }
        catch (BCrypt.Net.SaltParseException)
        {
            return false;
        }
    }
}
