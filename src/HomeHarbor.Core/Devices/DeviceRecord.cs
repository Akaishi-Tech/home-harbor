namespace HomeHarbor.Core.Devices;

public sealed record DeviceRecord(
    Guid Id,
    Guid FamilyId,
    string DisplayName,
    string Kind,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastSeenAt);

