using AgentForge.Domain.Primitives;

namespace AgentForge.Domain.Auditing;

public sealed record AuditRecordRequest(
    InstallationId? InstallationId,
    ActorId ActorId,
    CorrelationId CorrelationId,
    CorrelationId? CausationId,
    string OperationType,
    AuditOutcome Outcome,
    object? Input,
    object? Output,
    string? ErrorClassification);

public sealed record AuditRecordResult(
    AuditEventRecord Event,
    int InputRedactionCount,
    int OutputRedactionCount);

public sealed record AuditVerificationResult(
    bool IsValid,
    long VerifiedEventCount,
    long? BrokenSequence,
    string? FailureReason,
    string HeadHash);
