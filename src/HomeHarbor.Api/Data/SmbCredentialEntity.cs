namespace HomeHarbor.Api.Data;

public sealed class SmbCredentialEntity
{
    public Guid Id { get; set; }
    public Guid FamilyId { get; set; }
    public Guid ShareId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string UnixUser { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public bool ReadOnly { get; set; }
    public bool Enabled { get; set; }
    public string RuntimeState { get; set; } = "pending";
    public string LastError { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset RotatedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public DateTimeOffset? LastAppliedAt { get; set; }
}
