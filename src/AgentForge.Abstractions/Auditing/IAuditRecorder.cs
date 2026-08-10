using AgentForge.Domain.Auditing;

namespace AgentForge.Abstractions.Auditing;

public interface IAuditRecorder
{
    Task<AuditRecordResult> RecordAsync(AuditRecordRequest request, CancellationToken cancellationToken);
}

public interface IAuditIntegrityVerifier
{
    Task<AuditVerificationResult> VerifyAsync(CancellationToken cancellationToken);
}
