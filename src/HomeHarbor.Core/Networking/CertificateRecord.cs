namespace HomeHarbor.Core.Networking;

public sealed record CertificateRecord(
    Guid Id,
    Guid FamilyId,
    string Hostname,
    string Kind,
    string CertificatePem,
    string PrivateKeyPem,
    DateTimeOffset NotBefore,
    DateTimeOffset NotAfter,
    DateTimeOffset CreatedAt);

public sealed record ReverseProxyRoute(
    Guid Id,
    Guid FamilyId,
    string Hostname,
    string UpstreamUrl,
    bool TlsEnabled,
    DateTimeOffset CreatedAt);

