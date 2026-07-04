namespace HomeHarbor.Api.Data;

public sealed class StorageHealthEntity
{
    public Guid Id { get; set; }
    public Guid FamilyId { get; set; }
    public string DataRoot { get; set; } = string.Empty;
    public long TotalBytes { get; set; }
    public long AvailableBytes { get; set; }
    public string Status { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
    public DateTimeOffset CheckedAt { get; set; }
}

