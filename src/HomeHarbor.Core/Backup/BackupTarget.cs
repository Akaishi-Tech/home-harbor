namespace HomeHarbor.Core.Backup;

public sealed record BackupTarget(
    Guid Id,
    Guid FamilyId,
    string Name,
    string RepositoryUri,
    bool EncryptionEnabled,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastVerifiedAt);

