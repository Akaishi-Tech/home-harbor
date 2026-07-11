using System.Globalization;
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
    UrlEncoder encoder,
    AuthenticationFailureThrottle throttle) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Basic";
    public const string FamilyIdClaim = AuthClaims.FamilyId;
    public const string DeviceIdClaim = AuthClaims.DeviceId;
    public const string WebDavScopeClaim = AuthClaims.WebDavScope;

    private const string Realm = "HomeHarbor WebDAV";
    private const string ThrottledItem = "HomeHarbor.BasicAuthentication.Throttled";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("Authorization", out var headerValues))
            return AuthenticateResult.NoResult();

        if (!AuthenticationHeaderValue.TryParse(headerValues.ToString(), out var header) ||
            !string.Equals(header.Scheme, SchemeName, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(header.Parameter) ||
            header.Parameter.Length > 2048)
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
        if (username.Length is 0 or > 64 || password.Length is 0 or > 256)
            return AuthenticateResult.Fail("Invalid credentials.");
        var clientIdentity = AuthenticationClientIdentity.Resolve(Context);
        var throttleIdentity = username;
        var clientAllowed = throttle.TryAcquire("webdav-client", clientIdentity, out var clientRetryAfter);
        var accountAllowed = throttle.TryAcquire("webdav", throttleIdentity, out var accountRetryAfter);
        if (!clientAllowed || !accountAllowed)
        {
            var retryAfter = clientRetryAfter > accountRetryAfter ? clientRetryAfter : accountRetryAfter;
            Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
            Context.Items[ThrottledItem] = true;
            return AuthenticateResult.Fail("Too many failed authentication attempts. Try again later.");
        }

        var db = Context.RequestServices.GetRequiredService<HomeHarborDbContext>();
        var token = await db.WebDavTokens.FirstOrDefaultAsync(t => t.Username == username, Context.RequestAborted);
        if (token is null || !LocalPasswordVerifier.Verify(password, token.TokenHash))
        {
            throttle.RecordFailure("webdav-client", clientIdentity);
            throttle.RecordFailure("webdav", throttleIdentity);
            return AuthenticateResult.Fail("Invalid credentials.");
        }
        if (!Enum.IsDefined(token.Scope))
        {
            return AuthenticateResult.Fail("Credential scope is invalid. Revoke and reissue this credential.");
        }
        throttle.RecordSuccess("webdav", throttleIdentity);

        var now = DateTimeOffset.UtcNow;
        if (token.LastUsedAt is null || now - token.LastUsedAt >= TimeSpan.FromMinutes(5))
        {
            token.LastUsedAt = now;
            _ = await db.SaveChangesAsync(Context.RequestAborted);
        }

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
        if (Context.Items.ContainsKey(ThrottledItem))
        {
            Response.StatusCode = StatusCodes.Status429TooManyRequests;
            return Task.CompletedTask;
        }

        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = $"Basic realm=\"{Realm}\", charset=\"UTF-8\"";
        return Task.CompletedTask;
    }
}
