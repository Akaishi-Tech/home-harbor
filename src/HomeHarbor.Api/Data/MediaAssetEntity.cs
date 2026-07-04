namespace HomeHarbor.Api.Data;

public sealed class MediaAssetEntity
{
    public Guid Id { get; set; }
    public Guid FamilyId { get; set; }
    public string Area { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public string MediaType { get; set; } = string.Empty;
    public long Size { get; set; }
    public DateTimeOffset LastModifiedUtc { get; set; }
    public DateTimeOffset IndexedAt { get; set; }
}

