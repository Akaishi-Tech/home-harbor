namespace HomeHarbor.Api.Data;

public sealed class RecoveryDrillEntity
{
    public Guid Id { get; set; }
    public Guid FamilyId { get; set; }
    public Guid? BackupTargetId { get; set; }
    public string State { get; set; } = string.Empty;
    public string Result { get; set; } = string.Empty;
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
}

