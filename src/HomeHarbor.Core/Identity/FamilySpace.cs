namespace HomeHarbor.Core.Identity;

public sealed record FamilySpace(
    Guid Id,
    string Name,
    string OwnerDisplayName,
    DateTimeOffset CreatedAt);

