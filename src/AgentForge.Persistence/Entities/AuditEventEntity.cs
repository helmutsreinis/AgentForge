namespace AgentForge.Persistence.Entities;

internal sealed class AuditEventEntity
{
    public Guid EventId { get; set; }

    public long Sequence { get; set; }

    public DateTimeOffset Timestamp { get; set; }

    public Guid? InstallationId { get; set; }

    public string ActorId { get; set; } = string.Empty;

    public string CorrelationId { get; set; } = string.Empty;

    public string? CausationId { get; set; }

    public string OperationType { get; set; } = string.Empty;

    public string Outcome { get; set; } = string.Empty;

    public string InputJson { get; set; } = string.Empty;

    public string OutputJson { get; set; } = string.Empty;

    public string? ErrorClassification { get; set; }

    public string PreviousHash { get; set; } = string.Empty;

    public string EventHash { get; set; } = string.Empty;
}
