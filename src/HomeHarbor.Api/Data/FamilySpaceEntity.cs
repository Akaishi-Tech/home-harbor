namespace HomeHarbor.Api.Data;

public sealed class FamilySpaceEntity
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string OwnerDisplayName { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}

