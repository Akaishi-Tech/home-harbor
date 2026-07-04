namespace HomeHarbor.Api.Data;

public sealed class SyncStateEntity
{
    public Guid Id { get; set; }
    public Guid FamilyId { get; set; }
    public Guid DeviceId { get; set; }
    public string Scope { get; set; } = string.Empty;
    public string Cursor { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; set; }
}

