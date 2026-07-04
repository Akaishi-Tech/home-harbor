namespace HomeHarbor.Core.Recovery;

public sealed record RecoveryDrill(
    Guid Id,
    Guid FamilyId,
    Guid? BackupTargetId,
    string State,
    string Result,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt);

