namespace HomeHarbor.Api.Data;

public sealed class VaultItemEntity
{
    public Guid Id { get; set; }
    public Guid FamilyId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string EncryptedPayload { get; set; } = string.Empty;
    public string Nonce { get; set; } = string.Empty;
    public string KeyHint { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

