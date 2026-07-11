namespace HomeHarbor.Tooling;

public static class CaddyTrustConfiguration
{
    public const string ControlPlaneHostname = "homeharbor.local";
    public const string CertificateDownloadPath = "/homeharbor-ca.crt";
    public const string CertificateDownloadUrl = "http://homeharbor.local/homeharbor-ca.crt";
    public const string RootCertificateDirectory = "/var/lib/caddy/pki/authorities/local";
    public const string RootCertificatePath = RootCertificateDirectory + "/root.crt";

    public static string HttpSiteBlock()
        => $$"""
            :80 {
                @homeHarborCa path /homeharbor-ca.crt
                handle @homeHarborCa {
                    root * {{RootCertificateDirectory}}
                    rewrite * /root.crt
                    header Content-Type application/x-x509-ca-cert
                    header Content-Disposition "attachment; filename=homeharbor-ca.crt"
                    file_server
                }
                handle {
                    redir https://homeharbor.local{uri} permanent
                }
            }
            """.Replace("            ", string.Empty, StringComparison.Ordinal);
}
