namespace HomeHarbor.Core.Sync;

public sealed record DeviceSyncState(
    Guid Id,
    Guid FamilyId,
    Guid DeviceId,
    string Scope,
    string Cursor,
    DateTimeOffset UpdatedAt);

