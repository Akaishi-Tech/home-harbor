namespace HomeHarbor.Core.Identity;

public sealed record WebDavTokenRecord(
    Guid Id,
    Guid FamilyId,
    Guid? DeviceId,
    string Username,
    string TokenHash,
    WebDavTokenScope Scope,
    string? Description,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastUsedAt);

public enum WebDavTokenScope
{
    Files = 0,
    Photos = 1,
    Backups = 2,
    All = 3
}

