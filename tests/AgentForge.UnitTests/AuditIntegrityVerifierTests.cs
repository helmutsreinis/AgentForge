using AgentForge.Abstractions.Auditing;
using AgentForge.Audit;
using AgentForge.Domain.Auditing;
using AgentForge.Domain.Primitives;
using Microsoft.Extensions.DependencyInjection;

namespace AgentForge.UnitTests;

public sealed class AuditIntegrityVerifierTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 10, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Accepts_a_valid_contiguous_chain()
    {
        var events = CreateChain(2);

        var result = await VerifyAsync(events);

        Assert.True(result.IsValid);
        Assert.Equal(2, result.VerifiedEventCount);
        Assert.Equal(events[^1].EventHash, result.HeadHash);
    }

    [Fact]
    public async Task Rejects_tampered_content_at_the_exact_sequence()
    {
        var events = CreateChain(2).ToArray();
        events[1] = events[1] with { Output = new RedactedData("{\"changed\":true}") };

        var result = await VerifyAsync(events);

        Assert.False(result.IsValid);
        Assert.Equal(1, result.VerifiedEventCount);
        Assert.Equal(2, result.BrokenSequence);
        Assert.Contains("event hash", result.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Hash_encoding_is_unambiguous_across_field_boundaries()
    {
        var baseline = CreateChain(1)[0];
        var first = ToDraft(baseline) with
        {
            ActorId = new ActorId("actor\ncorrelation"),
            CorrelationId = new CorrelationId("tail"),
        };
        var second = ToDraft(baseline) with
        {
            ActorId = new ActorId("actor"),
            CorrelationId = new CorrelationId("correlation\ntail"),
        };

        var firstHash = AuditEventHasher.Compute(first, 1, AuditEventHasher.GenesisHash);
        var secondHash = AuditEventHasher.Compute(second, 1, AuditEventHasher.GenesisHash);

        Assert.NotEqual(firstHash, secondHash);
    }

    private static async Task<AuditVerificationResult> VerifyAsync(IReadOnlyList<AuditEventRecord> events)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAuditReader>(new FakeAuditReader(events));
        services.AddAgentForgeAudit();
        await using var provider = services.BuildServiceProvider(validateScopes: true);
        await using var scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IAuditIntegrityVerifier>()
            .VerifyAsync(CancellationToken.None);
    }

    private static List<AuditEventRecord> CreateChain(int count)
    {
        var events = new List<AuditEventRecord>();
        var previousHash = AuditEventHasher.GenesisHash;
        for (var index = 1; index <= count; index++)
        {
            var draft = new AuditEventDraft(
                Guid.Parse($"00000000-0000-0000-0000-{index:000000000000}"),
                Now.AddMinutes(index),
                null,
                new ActorId("operator"),
                new CorrelationId($"audit-{index}"),
                null,
                "audit.test",
                AuditOutcome.Succeeded,
                new RedactedData("{}"),
                new RedactedData("{}"),
                null);
            var eventHash = AuditEventHasher.Compute(draft, index, previousHash);
            events.Add(new AuditEventRecord(
                draft.EventId,
                index,
                draft.Timestamp,
                draft.InstallationId,
                draft.ActorId,
                draft.CorrelationId,
                draft.CausationId,
                draft.OperationType,
                draft.Outcome,
                draft.Input,
                draft.Output,
                draft.ErrorClassification,
                previousHash,
                eventHash));
            previousHash = eventHash;
        }

        return events;
    }

    private static AuditEventDraft ToDraft(AuditEventRecord auditEvent) => new(
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

    private sealed class FakeAuditReader(IReadOnlyList<AuditEventRecord> events) : IAuditReader
    {
        public Task<IReadOnlyList<AuditEventRecord>> ReadAsync(
            InstallationId? installationId,
            long afterSequence,
            int maximumCount,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<AuditEventRecord> result = events
                .Where(item => item.Sequence > afterSequence)
                .Take(maximumCount)
                .ToArray();
            return Task.FromResult(result);
        }
    }
}
