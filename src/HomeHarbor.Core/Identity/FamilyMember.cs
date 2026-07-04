namespace HomeHarbor.Core.Identity;

public sealed record FamilyMember(
    Guid Id,
    Guid FamilyId,
    string DisplayName,
    string Role,
    string? PasswordHash,
    DateTimeOffset CreatedAt);

public static class FamilyRoles
{
    public const string Owner = "owner";
    public const string Admin = "admin";
    public const string Member = "member";
    public const string Child = "child";
    public const string Guest = "guest";
}

