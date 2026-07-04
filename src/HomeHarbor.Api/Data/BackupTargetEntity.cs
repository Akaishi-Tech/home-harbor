namespace HomeHarbor.Api.Data;

public sealed class BackupTargetEntity
{
    public Guid Id { get; set; }
    public Guid FamilyId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string RepositoryUri { get; set; } = string.Empty;
    public bool EncryptionEnabled { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastVerifiedAt { get; set; }
}

