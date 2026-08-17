namespace AgentForge.Persistence.Entities;

internal sealed class RunConversationSnapshotEntity
{
    public Guid ConversationId { get; set; }

    public long Version { get; set; }

    public Guid InstallationId { get; set; }

    public Guid AgentId { get; set; }

    public Guid ProviderId { get; set; }

    public string State { get; set; } = string.Empty;

    public string PreviousSnapshotHash { get; set; } = string.Empty;

    public string SnapshotHash { get; set; } = string.Empty;

    public string SnapshotJson { get; set; } = string.Empty;

    public long CreatedAtUtcTicks { get; set; }

    public long UpdatedAtUtcTicks { get; set; }

    public string ActorId { get; set; } = string.Empty;

    public string IdempotencyKey { get; set; } = string.Empty;

    public string CorrelationId { get; set; } = string.Empty;

    public string? CausationId { get; set; }
}
