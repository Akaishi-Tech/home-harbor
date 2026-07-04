namespace HomeHarbor.Core.Apps;

public sealed record ManagedApp(
    Guid Id,
    Guid FamilyId,
    string AppKey,
    string Kind,
    string DisplayName,
    string Image,
    string State,
    string DesiredState,
    string RuntimeState,
    string InstalledVersion,
    string ActiveVersion,
    bool RequiresReboot,
    Guid? ContainerId,
    string LastError,
    string ManifestJson,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? LastAppliedAt);
