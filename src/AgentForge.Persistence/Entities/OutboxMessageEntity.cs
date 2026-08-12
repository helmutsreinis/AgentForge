namespace AgentForge.Persistence.Entities;

internal sealed class OutboxMessageEntity
{
    public Guid Id { get; set; }

    public DateTimeOffset OccurredAt { get; set; }

    public long OccurredAtUtcTicks { get; set; }

    public string MessageType { get; set; } = string.Empty;

    public string PayloadJson { get; set; } = string.Empty;

    public DateTimeOffset? ProcessedAt { get; set; }

    public int Attempts { get; set; }

    public long Version { get; set; }
}
