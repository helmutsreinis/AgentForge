using AgentForge.Abstractions.Auditing;
using AgentForge.Domain.Auditing;

namespace AgentForge.Audit;

internal sealed class AuditIntegrityVerifier(IAuditReader auditReader) : IAuditIntegrityVerifier
{
    private const int PageSize = 1000;

    public async Task<AuditVerificationResult> VerifyAsync(CancellationToken cancellationToken)
    {
        var expectedSequence = 1L;
        var expectedPreviousHash = AuditEventHasher.GenesisHash;
        var verifiedCount = 0L;

        while (true)
        {
            var page = await auditReader.ReadAsync(null, expectedSequence - 1, PageSize, cancellationToken);
            if (page.Count == 0)
            {
                return new AuditVerificationResult(
                    true,
                    verifiedCount,
                    null,
                    null,
                    expectedPreviousHash);
            }

            foreach (var auditEvent in page)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (auditEvent.Sequence != expectedSequence)
                {
                    return Invalid(verifiedCount, auditEvent.Sequence, "Audit sequence is not contiguous.", expectedPreviousHash);
                }

                if (!string.Equals(auditEvent.PreviousHash, expectedPreviousHash, StringComparison.Ordinal))
                {
                    return Invalid(verifiedCount, auditEvent.Sequence, "Audit previous-hash link does not match.", expectedPreviousHash);
                }

                var draft = new AuditEventDraft(
                    auditEvent.EventId,
                    auditEvent.Timestamp,
                    auditEvent.InstallationId,
                    auditEvent.ActorId,
                    auditEvent.CorrelationId,
                    auditEvent.CausationId,
                    auditEvent.OperationType,
                    auditEvent.Outcome,
                    auditEvent.Input,
                    auditEvent.Output,
                    auditEvent.ErrorClassification);
                var computedHash = AuditEventHasher.Compute(draft, auditEvent.Sequence, auditEvent.PreviousHash);
                if (!string.Equals(auditEvent.EventHash, computedHash, StringComparison.Ordinal))
                {
                    return Invalid(verifiedCount, auditEvent.Sequence, "Audit event hash does not match its content.", expectedPreviousHash);
                }

                expectedPreviousHash = auditEvent.EventHash;
                expectedSequence++;
                verifiedCount++;
            }
        }
    }

    private static AuditVerificationResult Invalid(
        long verifiedCount,
        long brokenSequence,
        string reason,
        string headHash) => new(false, verifiedCount, brokenSequence, reason, headHash);
}
