namespace HomeHarbor.Api.Data;

public sealed class ManagedAppEntity
{
    public Guid Id { get; set; }
    public Guid FamilyId { get; set; }
    public string AppKey { get; set; } = string.Empty;
    public string Kind { get; set; } = "container";
    public string DisplayName { get; set; } = string.Empty;
    public string Image { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string DesiredState { get; set; } = "installed";
    public string RuntimeState { get; set; } = "planned";
    public string InstalledVersion { get; set; } = string.Empty;
    public string ActiveVersion { get; set; } = string.Empty;
    public bool RequiresReboot { get; set; }
    public Guid? ContainerId { get; set; }
    public string LastError { get; set; } = string.Empty;
    public string ManifestJson { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? LastAppliedAt { get; set; }
}
