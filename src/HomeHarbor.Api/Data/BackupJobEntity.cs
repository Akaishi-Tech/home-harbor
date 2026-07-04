namespace HomeHarbor.Api.Data;

public sealed class BackupJobEntity
{
    public Guid Id { get; set; }
    public Guid FamilyId { get; set; }
    public Guid BackupTargetId { get; set; }
    public string State { get; set; } = string.Empty;
    public string Command { get; set; } = string.Empty;
    public string Result { get; set; } = string.Empty;
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
}

