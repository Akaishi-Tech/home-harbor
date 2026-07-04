using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace HomeHarbor.Api.Services;

public sealed class CertificateService : ICertificateService
{
    public GeneratedCertificate CreateSelfSigned(string hostname, TimeSpan lifetime)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            $"CN={hostname}",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
            false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(hostname);
        request.CertificateExtensions.Add(san.Build());

        var notBefore = DateTimeOffset.UtcNow.AddMinutes(-5);
        var notAfter = notBefore.Add(lifetime);
        using var certificate = request.CreateSelfSigned(notBefore, notAfter);
        return new GeneratedCertificate(
            certificate.ExportCertificatePem(),
            key.ExportPkcs8PrivateKeyPem(),
            notBefore,
            notAfter);
    }
}

public sealed record GeneratedCertificate(
    string CertificatePem,
    string PrivateKeyPem,
    DateTimeOffset NotBefore,
    DateTimeOffset NotAfter);
