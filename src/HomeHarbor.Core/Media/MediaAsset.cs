namespace HomeHarbor.Core.Media;

public sealed record MediaAsset(
    Guid Id,
    Guid FamilyId,
    string Area,
    string RelativePath,
    string MediaType,
    long Size,
    DateTimeOffset LastModifiedUtc,
    DateTimeOffset IndexedAt);

