using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using HomeHarbor.Api.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HomeHarbor.Api.Auth;

public sealed class BasicAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Basic";
    public const string FamilyIdClaim = AuthClaims.FamilyId;
    public const string DeviceIdClaim = AuthClaims.DeviceId;
    public const string WebDavScopeClaim = AuthClaims.WebDavScope;

    private const string Realm = "HomeHarbor WebDAV";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("Authorization", out var headerValues))
            return AuthenticateResult.NoResult();

        if (!AuthenticationHeaderValue.TryParse(headerValues.ToString(), out var header) ||
            !string.Equals(header.Scheme, SchemeName, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(header.Parameter))
        {
            return AuthenticateResult.NoResult();
        }

        string decoded;
        try
        {
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header.Parameter));
        }
        catch (FormatException)
        {
            return AuthenticateResult.Fail("Malformed Basic credentials.");
        }

        var separator = decoded.IndexOf(':');
        if (separator <= 0) return AuthenticateResult.Fail("Malformed Basic credentials.");

        var username = decoded[..separator];
        var password = decoded[(separator + 1)..];

        var db = Context.RequestServices.GetRequiredService<HomeHarborDbContext>();
        var token = await db.WebDavTokens.FirstOrDefaultAsync(t => t.Username == username, Context.RequestAborted);
        if (token is null) return AuthenticateResult.Fail("Invalid credentials.");

        bool verified;
        try
        {
            verified = BCrypt.Net.BCrypt.Verify(password, token.TokenHash);
        }
        catch (BCrypt.Net.SaltParseException)
        {
            return AuthenticateResult.Fail("Stored token hash is invalid.");
        }

        if (!verified) return AuthenticateResult.Fail("Invalid credentials.");

        token.LastUsedAt = DateTimeOffset.UtcNow;
        _ = await db.SaveChangesAsync(Context.RequestAborted);

        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, token.Username),
            new(FamilyIdClaim, token.FamilyId.ToString()),
            new(WebDavScopeClaim, token.Scope.ToString())
        };

        if (token.DeviceId is { } deviceId) claims.Add(new Claim(DeviceIdClaim, deviceId.ToString()));

        var identity = new ClaimsIdentity(claims, Scheme.Name);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = $"Basic realm=\"{Realm}\", charset=\"UTF-8\"";
        return Task.CompletedTask;
    }
}
