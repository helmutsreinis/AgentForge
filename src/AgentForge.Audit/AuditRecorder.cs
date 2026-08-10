using AgentForge.Abstractions.Auditing;
using AgentForge.Abstractions.Security;
using AgentForge.Abstractions.Time;
using AgentForge.Domain.Auditing;

namespace AgentForge.Audit;

internal sealed class AuditRecorder(
    IAuditSink auditSink,
    ISensitiveDataRedactor redactor,
    IClock clock,
    IIdentifierGenerator identifiers) : IAuditRecorder
{
    public async Task<AuditRecordResult> RecordAsync(
        AuditRecordRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OperationType);

        var input = redactor.Redact(request.Input);
        var output = redactor.Redact(request.Output);
        var auditEvent = await auditSink.AppendAsync(new AuditEventDraft(
            identifiers.NewGuid(),
            clock.UtcNow,
            request.InstallationId,
            request.ActorId,
            request.CorrelationId,
            request.CausationId,
            request.OperationType,
            request.Outcome,
            input.Data,
            output.Data,
            request.ErrorClassification), cancellationToken);

        return new AuditRecordResult(auditEvent, input.RedactionCount, output.RedactionCount);
    }
}
