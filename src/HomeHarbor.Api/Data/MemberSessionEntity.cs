namespace HomeHarbor.Api.Data;

public sealed class MemberSessionEntity
{
    public Guid Id { get; set; }
    public Guid FamilyId { get; set; }
    public Guid MemberId { get; set; }
    public string TokenHash { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}

