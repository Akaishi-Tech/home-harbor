namespace HomeHarbor.Api.Data;

public sealed class FamilyMemberEntity
{
    public Guid Id { get; set; }
    public Guid FamilyId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string? PasswordHash { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

