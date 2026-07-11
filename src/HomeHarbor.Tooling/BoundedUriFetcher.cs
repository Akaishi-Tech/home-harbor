using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;

namespace HomeHarbor.Tooling;

/// <summary>
/// Fetches release metadata and payloads without unbounded buffering, implicit redirects,
/// protocol downgrades, or unrestricted local-file access.
/// </summary>
public static class BoundedUriFetcher
{
    private const int MaxRedirects = 3;
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static HttpClient CreateHttpClient(TimeSpan timeout)
        => new(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            ConnectTimeout = timeout,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        })
        {
            Timeout = timeout
        };

    public static Uri ValidateUri(
        string uriText,
        Uri? sameOriginAs = null,
        string? allowedFileRoot = null,
        string label = "download URL")
    {
        if (string.IsNullOrWhiteSpace(uriText) || uriText.Length > 2048 ||
            !Uri.TryCreate(uriText, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException($"{label} must be an absolute URL");
        }

        if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new InvalidOperationException($"{label} cannot contain credentials or a fragment");
        }

        if (uri.Scheme == Uri.UriSchemeFile)
        {
            _ = ResolveAllowedFilePath(uri, allowedFileRoot, label);
            if (sameOriginAs is not null && sameOriginAs.Scheme != Uri.UriSchemeFile)
            {
                throw new InvalidOperationException($"{label} cannot change from a network origin to a local file");
            }

            return uri;
        }

        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException($"{label} must use HTTPS");
        }

        if (string.IsNullOrWhiteSpace(uri.IdnHost) || IsDisallowedHost(uri.IdnHost))
        {
            throw new InvalidOperationException($"{label} host is not allowed");
        }

        if (sameOriginAs is not null && !HasSameOrigin(uri, sameOriginAs))
        {
            throw new InvalidOperationException($"{label} must stay on the trusted origin {sameOriginAs.GetLeftPart(UriPartial.Authority)}");
        }

        return uri;
    }

    public static async Task<string> ReadUtf8TextAsync(
        HttpClient http,
        string uriText,
        long maxBytes,
        Uri? sameOriginAs = null,
        string? allowedFileRoot = null,
        string label = "download",
        CancellationToken cancellationToken = default)
    {
        await using var buffer = new MemoryStream();
        await CopyToAsync(http, uriText, buffer, maxBytes, sameOriginAs, allowedFileRoot, label, cancellationToken);
        return StrictUtf8.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length));
    }

    public static async Task DownloadToFileAsync(
        HttpClient http,
        string uriText,
        string destination,
        long maxBytes,
        Uri? sameOriginAs = null,
        string? allowedFileRoot = null,
        string label = "download",
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBytes, 1);

        var destinationFull = Path.GetFullPath(destination);
        var parent = Path.GetDirectoryName(destinationFull)
            ?? throw new InvalidOperationException($"{label} destination has no parent directory");
        _ = Directory.CreateDirectory(parent);
        var temporary = Path.Combine(parent, "." + Path.GetFileName(destinationFull) + ".download-" + Guid.NewGuid().ToString("N"));

        try
        {
            await using (var output = new FileStream(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             1024 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await CopyToAsync(http, uriText, output, maxBytes, sameOriginAs, allowedFileRoot, label, cancellationToken);
                await output.FlushAsync(cancellationToken);
            }

            File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
            File.Move(temporary, destinationFull, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
            }
        }
    }

    private static async Task CopyToAsync(
        HttpClient http,
        string uriText,
        Stream destination,
        long maxBytes,
        Uri? sameOriginAs,
        string? allowedFileRoot,
        string label,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBytes, 1);

        var initial = ValidateUri(uriText, sameOriginAs, allowedFileRoot, label + " URL");
        var trustedOrigin = sameOriginAs ?? initial;
        if (initial.Scheme == Uri.UriSchemeFile)
        {
            var path = ResolveAllowedFilePath(initial, allowedFileRoot, label + " URL");
            await using var input = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await CopyLimitedAsync(input, destination, maxBytes, label, cancellationToken);
            return;
        }

        var current = initial;
        for (var redirect = 0; ; redirect++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
            request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("deflate"));
            request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("br"));
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            var effectiveUri = response.RequestMessage?.RequestUri;
            if (effectiveUri is null || !UriEquals(effectiveUri, current))
            {
                throw new InvalidOperationException($"{label} HTTP client followed a redirect implicitly; automatic redirects must be disabled");
            }

            if (IsRedirect(response.StatusCode))
            {
                if (redirect >= MaxRedirects)
                {
                    throw new InvalidOperationException($"{label} exceeded the maximum redirect count");
                }

                var location = response.Headers.Location
                    ?? throw new InvalidOperationException($"{label} redirect did not include a Location header");
                var next = location.IsAbsoluteUri ? location : new Uri(current, location);
                current = ValidateUri(next.AbsoluteUri, trustedOrigin, allowedFileRoot, label + " redirect URL");
                continue;
            }

            _ = response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is { } contentLength &&
                (contentLength < 0 || contentLength > maxBytes))
            {
                throw new InvalidOperationException($"{label} exceeds the {maxBytes}-byte limit");
            }

            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            await CopyLimitedAsync(input, destination, maxBytes, label, cancellationToken);
            return;
        }
    }

    private static async Task CopyLimitedAsync(
        Stream input,
        Stream output,
        long maxBytes,
        string label,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[1024 * 1024];
        long copied = 0;
        while (true)
        {
            var remaining = maxBytes - copied;
            var requested = (int)Math.Min(buffer.Length, remaining + 1);
            var read = await input.ReadAsync(buffer.AsMemory(0, requested), cancellationToken);
            if (read == 0)
            {
                return;
            }

            if (read > remaining)
            {
                throw new InvalidOperationException($"{label} exceeds the {maxBytes}-byte limit");
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            copied += read;
        }
    }

    private static string ResolveAllowedFilePath(Uri uri, string? allowedFileRoot, string label)
    {
        if (string.IsNullOrWhiteSpace(allowedFileRoot))
        {
            throw new InvalidOperationException($"{label} uses file: but no local file root was explicitly configured");
        }

        if (!string.IsNullOrEmpty(uri.Host))
        {
            throw new InvalidOperationException($"{label} cannot use a remote file host");
        }

        var root = ResolveExistingLinks(Path.GetFullPath(allowedFileRoot));
        var candidate = ResolveExistingLinks(Path.GetFullPath(uri.LocalPath));
        if (!IsInside(candidate, root) || string.Equals(candidate, root, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{label} escapes the configured local file root");
        }

        if (!File.Exists(candidate))
        {
            throw new FileNotFoundException($"{label} local file was not found", candidate);
        }

        return candidate;
    }

    private static string ResolveExistingLinks(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(root))
        {
            return full;
        }

        var resolved = root;
        foreach (var segment in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            var next = Path.Combine(resolved, segment);
            FileSystemInfo info = Directory.Exists(next) ? new DirectoryInfo(next) : new FileInfo(next);
            FileSystemInfo? target;
            try
            {
                target = info.ResolveLinkTarget(returnFinalTarget: true);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                target = null;
            }

            resolved = target is null ? next : Path.GetFullPath(target.FullName);
        }

        return Path.GetFullPath(resolved);
    }

    private static bool IsInside(string candidate, string root)
    {
        var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar);
        if (normalizedRoot.Length == 0)
        {
            normalizedRoot = Path.DirectorySeparatorChar.ToString();
        }

        return candidate.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static bool HasSameOrigin(Uri candidate, Uri trusted)
        => candidate.Scheme == Uri.UriSchemeHttps &&
           trusted.Scheme == Uri.UriSchemeHttps &&
           string.Equals(candidate.IdnHost, trusted.IdnHost, StringComparison.OrdinalIgnoreCase) &&
           candidate.Port == trusted.Port;

    private static bool UriEquals(Uri left, Uri right)
        => string.Equals(left.AbsoluteUri, right.AbsoluteUri, StringComparison.Ordinal);

    private static bool IsRedirect(HttpStatusCode statusCode)
        => statusCode is HttpStatusCode.MovedPermanently or
            HttpStatusCode.Redirect or
            HttpStatusCode.RedirectMethod or
            HttpStatusCode.TemporaryRedirect or
            HttpStatusCode.PermanentRedirect;

    private static bool IsDisallowedHost(string host)
    {
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IPAddress.TryParse(host, out var address) && !IsPublicAddress(address);
    }

    private static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var bytes = address.GetAddressBytes();
            return !IPAddress.IsLoopback(address) &&
                   !address.IsIPv6LinkLocal &&
                   !address.IsIPv6Multicast &&
                   !address.IsIPv6SiteLocal &&
                   !address.Equals(IPAddress.IPv6Any) &&
                   (bytes[0] & 0xfe) != 0xfc;
        }

        var octets = address.GetAddressBytes();
        return !IPAddress.IsLoopback(address) &&
               !address.Equals(IPAddress.Any) &&
               octets[0] != 0 &&
               octets[0] != 10 &&
               octets[0] != 127 &&
               !(octets[0] == 100 && octets[1] is >= 64 and <= 127) &&
               !(octets[0] == 169 && octets[1] == 254) &&
               !(octets[0] == 172 && octets[1] is >= 16 and <= 31) &&
               !(octets[0] == 192 && octets[1] == 168) &&
               !(octets[0] == 198 && octets[1] is 18 or 19) &&
               octets[0] < 224;
    }
}
