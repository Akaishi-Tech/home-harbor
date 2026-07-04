namespace HomeHarbor.Api.Data;

public sealed class ManagedContainerEntity
{
    public Guid Id { get; set; }
    public Guid FamilyId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Image { get; set; } = string.Empty;
    public string DesiredState { get; set; } = "stopped";
    public string RuntimeState { get; set; } = "planned";
    public string RequestedAction { get; set; } = "none";
    public string ServiceName { get; set; } = string.Empty;
    public string DefinitionJson { get; set; } = "{}";
    public string LastError { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public DateTimeOffset? LastAppliedAt { get; set; }
}
