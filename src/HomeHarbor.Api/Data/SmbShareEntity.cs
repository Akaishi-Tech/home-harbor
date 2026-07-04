namespace HomeHarbor.Api.Data;

public sealed class SmbShareEntity
{
    public Guid Id { get; set; }
    public Guid FamilyId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string ShareName { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public bool ReadOnly { get; set; }
    public bool Enabled { get; set; }
    public string RuntimeState { get; set; } = "planned";
    public string LastError { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? LastAppliedAt { get; set; }
}
