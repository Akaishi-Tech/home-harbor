namespace HomeHarbor.Core.Storage;

public sealed record StorageHealthSnapshot(
    Guid Id,
    Guid FamilyId,
    string DataRoot,
    long TotalBytes,
    long AvailableBytes,
    string Status,
    string Notes,
    DateTimeOffset CheckedAt);

