using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using HomeHarbor.Api.Auth;
using HomeHarbor.Api.Data;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace HomeHarbor.Api.Services;

public sealed class JwtTokenService(
    IOptions<HomeHarborJwtOptions> jwtOptions,
    IOptions<HomeHarborAutomationOptions> automationOptions,
    ITokenGenerator tokenGenerator) : IJwtTokenService
{
    public TimeSpan UserAccessTokenLifetime => TimeSpan.FromDays(Math.Max(1, jwtOptions.Value.AccessTokenDays));

    public string GenerateTokenId() => tokenGenerator.GenerateSecret(32);

    public string IssueUserAccessToken(
        MemberSessionEntity session,
        FamilyMemberEntity member,
        FamilySpaceEntity family,
        string tokenId)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, member.Id.ToString()),
            new(JwtRegisteredClaimNames.Jti, tokenId),
            new(AuthClaims.SessionId, session.Id.ToString()),
            new(AuthClaims.FamilyId, family.Id.ToString()),
            new(AuthClaims.TokenKind, AuthTokenKinds.User),
            new(ClaimTypes.Name, member.DisplayName),
            new(ClaimTypes.Role, member.Role),
            new("role", member.Role)
        };

        return IssueToken(claims, session.ExpiresAt);
    }

    public string IssueAutomationToken()
    {
        var expiresAt = DateTimeOffset.UtcNow.AddDays(Math.Max(1, automationOptions.Value.TokenDays));
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, "homeharbor-automation"),
            new(JwtRegisteredClaimNames.Jti, GenerateTokenId()),
            new(AuthClaims.TokenKind, AuthTokenKinds.Automation)
        };

        return IssueToken(claims, expiresAt);
    }

    public async Task WriteAutomationTokenAsync(CancellationToken cancellationToken = default)
    {
        var path = automationOptions.Value.TokenPath;
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException("HomeHarbor:Automation:TokenPath is required.");

        _ = Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        await File.WriteAllTextAsync(path, IssueAutomationToken(), cancellationToken);
        TryRestrictPermissions(path);
    }

    public static string HashTokenId(string tokenId)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(tokenId)));

    private string IssueToken(IEnumerable<Claim> claims, DateTimeOffset expiresAt)
    {
        var options = jwtOptions.Value;
        var now = DateTimeOffset.UtcNow;
        var signingKey = JwtSigningKeyStore.GetOrCreateSecurityKey(options.SigningKeyPath);
        var credentials = new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer: options.Issuer,
            audience: options.Audience,
            claims: claims.Concat([
                new Claim(JwtRegisteredClaimNames.Iat, now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture), ClaimValueTypes.Integer64)
            ]),
            notBefore: now.UtcDateTime,
            expires: expiresAt.UtcDateTime,
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
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
    }
}
