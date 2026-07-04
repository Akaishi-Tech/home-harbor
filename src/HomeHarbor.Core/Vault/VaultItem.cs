namespace HomeHarbor.Core.Vault;

public sealed record VaultItem(
    Guid Id,
    Guid FamilyId,
    string Name,
    string EncryptedPayload,
    string Nonce,
    string KeyHint,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

