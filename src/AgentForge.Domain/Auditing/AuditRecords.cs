using AgentForge.Domain.Primitives;

namespace AgentForge.Domain.Auditing;

public enum AuditOutcome
{
    Succeeded,
    Denied,
    Failed,
    Canceled,
}

public sealed record RedactedData(string Json)
{
    public static RedactedData Empty { get; } = new("{}");
}

public sealed record AuditEventDraft(
    Guid EventId,
    DateTimeOffset Timestamp,
    InstallationId? InstallationId,
    ActorId ActorId,
    CorrelationId CorrelationId,
    CorrelationId? CausationId,
    string OperationType,
    AuditOutcome Outcome,
    RedactedData Input,
    RedactedData Output,
    string? ErrorClassification);

public sealed record AuditEventRecord(
    Guid EventId,
    long Sequence,
    DateTimeOffset Timestamp,
    InstallationId? InstallationId,
    ActorId ActorId,
    CorrelationId CorrelationId,
    CorrelationId? CausationId,
    string OperationType,
    AuditOutcome Outcome,
    RedactedData Input,
    RedactedData Output,
    string? ErrorClassification,
    string PreviousHash,
    string EventHash);
