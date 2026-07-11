using System.Net;

namespace HomeHarbor.Api.Auth;

public static class AuthenticationClientIdentity
{
    public static string Resolve(HttpContext context)
    {
        if (context.Connection.RemoteIpAddress is { } remoteAddress)
            return remoteAddress.ToString();

        // Production Kestrel listens on a protected Unix socket. Only for that transport do we
        // accept the address supplied by the local reverse proxy; TCP clients cannot spoof it.
        var forwarded = context.Request.Headers["X-Forwarded-For"].ToString().Split(',')[0].Trim();
        return IPAddress.TryParse(forwarded, out var forwardedAddress)
            ? forwardedAddress.ToString()
            : "local-unix-socket";
    }
}
