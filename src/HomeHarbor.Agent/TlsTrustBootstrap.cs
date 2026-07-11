using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using HomeHarbor.Tooling;

internal static class TlsTrustBootstrap
{
    public static async Task<string> DisplayAsync(
        string certificatePath,
        IReadOnlyList<string> consoles,
        CancellationToken cancellationToken)
    {
        var path = RootPathGuard.RequireNoSymlinkComponents(certificatePath, "Caddy root CA certificate");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Caddy root CA certificate is not ready", path);
        }

        using var certificate = X509CertificateLoader.LoadCertificateFromFile(path);
        var constraints = certificate.Extensions
            .OfType<X509BasicConstraintsExtension>()
            .FirstOrDefault();
        if (constraints?.CertificateAuthority != true)
        {
            throw new InvalidOperationException("Caddy trust bootstrap certificate is not a CA certificate");
        }

        var fingerprint = FormatFingerprint(SHA256.HashData(certificate.RawData));
        var message = Environment.NewLine +
            "HomeHarbor secure browser trust" + Environment.NewLine +
            "Download the public CA certificate: " + CaddyTrustConfiguration.CertificateDownloadUrl + Environment.NewLine +
            "Verify its SHA-256 fingerprint on the client:" + Environment.NewLine +
            fingerprint + Environment.NewLine +
            "Install it only if the fingerprint matches. Do not enter the setup code or a password before the browser trusts HomeHarbor." +
            Environment.NewLine + Environment.NewLine;

        var displayed = 0;
        var failures = new List<string>();
        foreach (var console in consoles.Distinct(StringComparer.Ordinal))
        {
            try
            {
                await using var stream = new FileStream(console, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
                await stream.WriteAsync(Encoding.UTF8.GetBytes(message), cancellationToken);
                await stream.FlushAsync(cancellationToken);
                displayed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileNotFoundException or DirectoryNotFoundException)
            {
                failures.Add(console + ": " + ex.Message);
            }
        }

        if (displayed == 0)
        {
            throw new InvalidOperationException(
                "HomeHarbor CA fingerprint could not be displayed on a physical console" +
                (failures.Count == 0 ? string.Empty : ": " + string.Join("; ", failures)));
        }

        return fingerprint;
    }

    internal static string FormatFingerprint(ReadOnlySpan<byte> digest)
        => string.Join(':', digest.ToArray().Select(value => value.ToString("X2", System.Globalization.CultureInfo.InvariantCulture)));
}
