namespace HomeHarbor.Api.Data;

public sealed class DeviceEntity
{
    public Guid Id { get; set; }
    public Guid FamilyId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastSeenAt { get; set; }
}

