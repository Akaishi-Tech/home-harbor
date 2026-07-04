using System.Globalization;
using System.Net;
using System.Text;
using HomeHarbor.Api.Data;
using Microsoft.Extensions.Options;

namespace HomeHarbor.Api.Services;

public sealed class ReverseProxyConfigService(IOptions<HomeHarborApiOptions> apiOptions) : IReverseProxyConfigService
{
    public string BuildCaddyfile(IEnumerable<ReverseProxyRouteEntity> routes)
    {
        var apiUpstream = NormalizeUpstreamUrlOrThrow(apiOptions.Value.CaddyUpstream);
        var builder = new StringBuilder();
        _ = builder.AppendLine("{");
        _ = builder.AppendLine("    auto_https disable_redirects");
        _ = builder.AppendLine("}");
        _ = builder.AppendLine();

        _ = builder.AppendLine(":80 {");
        AppendReverseProxy(builder, apiUpstream);
        _ = builder.AppendLine("}");
        _ = builder.AppendLine();

        var orderedRoutes = routes
            .Select(NormalizeStoredRoute)
            .OrderBy(r => r.Hostname, StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var route in orderedRoutes)
        {
            _ = builder.AppendLine(CultureInfo.InvariantCulture, $"{route.Hostname} {{");
            if (!route.TlsEnabled) _ = builder.AppendLine("    tls internal");
            AppendReverseProxy(builder, route.UpstreamUrl);
            _ = builder.AppendLine("}");
            _ = builder.AppendLine();
        }

        if (orderedRoutes.Count == 0)
        {
            _ = builder.AppendLine("homeharbor.local {");
            _ = builder.AppendLine("    tls internal");
            AppendReverseProxy(builder, apiUpstream);
            _ = builder.AppendLine("}");
        }

        return builder.ToString();
    }

    public static bool TryNormalizeHostname(string? hostname, out string normalized, out string error)
    {
        try
        {
            normalized = NormalizeHostnameOrThrow(hostname);
            error = string.Empty;
            return true;
        }
        catch (InvalidOperationException ex)
        {
            normalized = string.Empty;
            error = ex.Message;
            return false;
        }
    }

    public static bool TryNormalizeUpstreamUrl(string? upstreamUrl, out string normalized, out string error)
    {
        try
        {
            normalized = NormalizeUpstreamUrlOrThrow(upstreamUrl);
            error = string.Empty;
            return true;
        }
        catch (InvalidOperationException ex)
        {
            normalized = string.Empty;
            error = ex.Message;
            return false;
        }
    }

    private static NormalizedRoute NormalizeStoredRoute(ReverseProxyRouteEntity route)
    {
        try
        {
            return new NormalizedRoute(
                NormalizeHostnameOrThrow(route.Hostname),
                NormalizeUpstreamUrlOrThrow(route.UpstreamUrl),
                route.TlsEnabled);
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException($"Reverse proxy route {route.Id} is invalid: {ex.Message}", ex);
        }
    }

    private static string NormalizeHostnameOrThrow(string? hostname)
    {
        if (string.IsNullOrWhiteSpace(hostname))
        {
            throw new InvalidOperationException("hostname is required.");
        }

        if (ContainsWhitespaceOrControl(hostname))
        {
            throw new InvalidOperationException("hostname must not contain whitespace or control characters.");
        }

        var normalized = hostname.ToLowerInvariant();
        if (normalized.Contains('/', StringComparison.Ordinal) ||
            normalized.Contains('\\', StringComparison.Ordinal) ||
            normalized.Contains(':', StringComparison.Ordinal))
        {
            throw new InvalidOperationException("hostname must not include a path or port.");
        }

        ValidateDnsHostname(normalized, "hostname");
        return normalized;
    }

    private static string NormalizeUpstreamUrlOrThrow(string? upstreamUrl)
    {
        if (string.IsNullOrWhiteSpace(upstreamUrl))
        {
            throw new InvalidOperationException("upstreamUrl is required.");
        }

        if (ContainsWhitespaceOrControl(upstreamUrl))
        {
            throw new InvalidOperationException("upstreamUrl must not contain whitespace or control characters.");
        }

        var normalized = upstreamUrl.Trim();
        if (ContainsCaddyfileDelimiter(normalized))
        {
            throw new InvalidOperationException("upstreamUrl must not contain Caddyfile block delimiters.");
        }

        if (normalized.StartsWith("unix//", StringComparison.Ordinal))
        {
            return normalized.Length == "unix//".Length
                ? throw new InvalidOperationException("upstreamUrl unix socket path is required.")
                : normalized;
        }

        if (normalized.Contains("://", StringComparison.Ordinal))
        {
            ValidateUrlUpstream(normalized);
            return normalized;
        }

        ValidateBareUpstream(normalized);
        return normalized;
    }

    private static void ValidateUrlUpstream(string upstreamUrl)
    {
        if (!Uri.TryCreate(upstreamUrl, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException("upstreamUrl must be a valid URL.");
        }

        if (!IsAllowedUrlScheme(uri.Scheme))
        {
            throw new InvalidOperationException("upstreamUrl scheme must be http, https, h2c, or unix//.");
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new InvalidOperationException("upstreamUrl must not include user info.");
        }

        ValidateUpstreamHost(uri.Host);
        ValidatePort(uri.Port);
    }

    private static void ValidateBareUpstream(string upstreamUrl)
    {
        if (upstreamUrl.Contains('/', StringComparison.Ordinal) ||
            upstreamUrl.Contains('\\', StringComparison.Ordinal))
        {
            throw new InvalidOperationException("upstreamUrl paths require an http://, https://, or h2c:// scheme.");
        }

        if (upstreamUrl.StartsWith('['))
        {
            ValidateBracketedIpUpstream(upstreamUrl);
            return;
        }

        if (IPAddress.TryParse(upstreamUrl, out _))
        {
            return;
        }

        var colonCount = upstreamUrl.Count(c => c == ':');
        if (colonCount > 1)
        {
            throw new InvalidOperationException("upstreamUrl IPv6 addresses with ports must use brackets.");
        }

        var host = upstreamUrl;
        var port = -1;
        if (colonCount == 1)
        {
            var separator = upstreamUrl.LastIndexOf(':');
            host = upstreamUrl[..separator];
            port = ParsePort(upstreamUrl[(separator + 1)..]);
        }

        ValidateUpstreamHost(host);
        ValidatePort(port);
    }

    private static void ValidateBracketedIpUpstream(string upstreamUrl)
    {
        var closeBracket = upstreamUrl.IndexOf(']', StringComparison.Ordinal);
        if (closeBracket <= 1)
        {
            throw new InvalidOperationException("upstreamUrl IPv6 host is invalid.");
        }

        var host = upstreamUrl[1..closeBracket];
        if (!IPAddress.TryParse(host, out _))
        {
            throw new InvalidOperationException("upstreamUrl host is invalid.");
        }

        var remainder = upstreamUrl[(closeBracket + 1)..];
        if (remainder.Length == 0)
        {
            return;
        }

        if (!remainder.StartsWith(':'))
        {
            throw new InvalidOperationException("upstreamUrl bracketed IPv6 host may only be followed by a port.");
        }

        ValidatePort(ParsePort(remainder[1..]));
    }

    private static void ValidateUpstreamHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            throw new InvalidOperationException("upstreamUrl host is required.");
        }

        if (IPAddress.TryParse(host, out _))
        {
            return;
        }

        ValidateDnsHostname(host.ToLowerInvariant(), "upstreamUrl host");
    }

    private static void ValidateDnsHostname(string hostname, string fieldName)
    {
        if (hostname.Length > 253 ||
            hostname.StartsWith('.') ||
            hostname.EndsWith('.') ||
            hostname.Contains("..", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{fieldName} must contain DNS labels separated by dots.");
        }

        foreach (var label in hostname.Split('.'))
        {
            if (label.Length is 0 or > 63 ||
                label.StartsWith('-') ||
                label.EndsWith('-'))
            {
                throw new InvalidOperationException($"{fieldName} must contain DNS labels separated by dots.");
            }

            foreach (var character in label)
            {
                if (!IsDnsLabelCharacter(character))
                {
                    throw new InvalidOperationException($"{fieldName} must contain only letters, numbers, hyphens, and dots.");
                }
            }
        }
    }

    private static int ParsePort(string value)
    {
        return !int.TryParse(value, out var port) ? throw new InvalidOperationException("upstreamUrl port is invalid.") : port;
    }

    private static void ValidatePort(int port)
    {
        if (port is > 0 and <= 65535 or -1)
        {
            return;
        }

        throw new InvalidOperationException("upstreamUrl port is invalid.");
    }

    private static bool IsAllowedUrlScheme(string scheme)
        => string.Equals(scheme, "http", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(scheme, "https", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(scheme, "h2c", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsWhitespaceOrControl(string value)
        => value.Any(character => char.IsWhiteSpace(character) || char.IsControl(character));

    private static bool ContainsCaddyfileDelimiter(string value)
        => value.Contains('{', StringComparison.Ordinal) || value.Contains('}', StringComparison.Ordinal);

    private static bool IsDnsLabelCharacter(char character)
        => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-';

    private static void AppendReverseProxy(StringBuilder builder, string upstream)
    {
        if (!upstream.StartsWith("unix//", StringComparison.Ordinal))
        {
            _ = builder.AppendLine(CultureInfo.InvariantCulture, $"    reverse_proxy {upstream}");
            return;
        }

        _ = builder.AppendLine(CultureInfo.InvariantCulture, $"    reverse_proxy {upstream} {{");
        _ = builder.AppendLine("        header_up Host {host}");
        _ = builder.AppendLine("    }");
    }

    private sealed record NormalizedRoute(string Hostname, string UpstreamUrl, bool TlsEnabled);
}
