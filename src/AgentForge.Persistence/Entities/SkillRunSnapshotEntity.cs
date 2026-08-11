namespace AgentForge.Persistence.Entities;

internal sealed class SkillRunSnapshotEntity
{
    public Guid Id { get; set; }
    public Guid InstallationId { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;
    public string SnapshotHash { get; set; } = string.Empty;
    public string SnapshotJson { get; set; } = string.Empty;
    public long CreatedAtUtcTicks { get; set; }
    public string ActorId { get; set; } = string.Empty;
    public string CorrelationId { get; set; } = string.Empty;
    public string? CausationId { get; set; }
}
