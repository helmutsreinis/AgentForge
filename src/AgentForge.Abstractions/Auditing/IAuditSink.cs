using AgentForge.Domain.Auditing;
using AgentForge.Domain.Primitives;

namespace AgentForge.Abstractions.Auditing;

public interface IAuditSink
{
    Task<AuditEventRecord> AppendAsync(AuditEventDraft auditEvent, CancellationToken cancellationToken);
}

public interface IAuditReader
{
    Task<IReadOnlyList<AuditEventRecord>> ReadAsync(
        InstallationId? installationId,
        long afterSequence,
        int maximumCount,
        CancellationToken cancellationToken);
}
