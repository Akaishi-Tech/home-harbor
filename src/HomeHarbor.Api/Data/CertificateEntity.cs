namespace HomeHarbor.Api.Data;

public sealed class CertificateEntity
{
    public Guid Id { get; set; }
    public Guid FamilyId { get; set; }
    public string Hostname { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string CertificatePem { get; set; } = string.Empty;
    public string PrivateKeyPem { get; set; } = string.Empty;
    public DateTimeOffset NotBefore { get; set; }
    public DateTimeOffset NotAfter { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

