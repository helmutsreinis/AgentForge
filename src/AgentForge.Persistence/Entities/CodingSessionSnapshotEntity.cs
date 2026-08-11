namespace AgentForge.Persistence.Entities;

internal sealed class CodingSessionSnapshotEntity
{
    public Guid SessionId { get; set; }
    public long Version { get; set; }
    public Guid InstallationId { get; set; }
    public Guid AgentId { get; set; }
    public string State { get; set; } = string.Empty;
    public string PreviousSnapshotHash { get; set; } = string.Empty;
    public string SnapshotHash { get; set; } = string.Empty;
    public string SnapshotJson { get; set; } = string.Empty;
    public long UpdatedAtUtcTicks { get; set; }
    public string ActorId { get; set; } = string.Empty;
    public string IdempotencyKey { get; set; } = string.Empty;
    public string CorrelationId { get; set; } = string.Empty;
    public string? CausationId { get; set; }
}
