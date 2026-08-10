using System.Text.Json;
using AgentForge.Abstractions.Auditing;
using AgentForge.Domain.Auditing;
using AgentForge.Domain.Primitives;
using AgentForge.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace AgentForge.Persistence;

internal sealed class SqliteAuditJournal(AgentForgeDbContext dbContext) : IAuditSink, IAuditReader
{
    public async Task<AuditEventRecord> AppendAsync(
        AuditEventDraft auditEvent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);
        Validate(auditEvent);

        var pendingLast = dbContext.ChangeTracker.Entries<AuditEventEntity>()
            .Where(entry => entry.State is EntityState.Added)
            .Select(entry => entry.Entity)
            .OrderByDescending(entity => entity.Sequence)
            .FirstOrDefault();
        var persistedLast = await dbContext.AuditEvents
            .AsNoTracking()
            .OrderByDescending(entity => entity.Sequence)
            .FirstOrDefaultAsync(cancellationToken);
        var last = pendingLast is not null && (persistedLast is null || pendingLast.Sequence > persistedLast.Sequence)
            ? pendingLast
            : persistedLast;

        var sequence = checked((last?.Sequence ?? 0) + 1);
        var previousHash = last?.EventHash ?? AuditHashChain.GenesisHash;
        var eventHash = AuditHashChain.Compute(auditEvent, sequence, previousHash);
        var entity = Map(auditEvent, sequence, previousHash, eventHash);
        await dbContext.AuditEvents.AddAsync(entity, cancellationToken);
        return Map(entity);
    }

    public async Task<IReadOnlyList<AuditEventRecord>> ReadAsync(
        InstallationId? installationId,
        long afterSequence,
        int maximumCount,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(afterSequence);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumCount, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumCount, 1000);

        var query = dbContext.AuditEvents.AsNoTracking().Where(entity => entity.Sequence > afterSequence);
        if (installationId is not null)
        {
            var value = installationId.Value.Value;
            query = query.Where(entity => entity.InstallationId == value);
        }

        return await query
            .OrderBy(entity => entity.Sequence)
            .Take(maximumCount)
            .Select(entity => Map(entity))
            .ToListAsync(cancellationToken);
    }

    private static void Validate(AuditEventDraft auditEvent)
    {
        if (auditEvent.EventId == Guid.Empty)
        {
            throw new ArgumentException("Audit event ID cannot be empty.", nameof(auditEvent));
        }

        if (string.IsNullOrWhiteSpace(auditEvent.OperationType))
        {
            throw new ArgumentException("Audit operation type is required.", nameof(auditEvent));
        }

        using var input = JsonDocument.Parse(auditEvent.Input.Json);
        using var output = JsonDocument.Parse(auditEvent.Output.Json);
    }

    private static AuditEventEntity Map(
        AuditEventDraft auditEvent,
        long sequence,
        string previousHash,
        string eventHash) => new()
        {
            EventId = auditEvent.EventId,
            Sequence = sequence,
            Timestamp = auditEvent.Timestamp,
            InstallationId = auditEvent.InstallationId?.Value,
            ActorId = auditEvent.ActorId.Value,
            CorrelationId = auditEvent.CorrelationId.Value,
            CausationId = auditEvent.CausationId?.Value,
            OperationType = auditEvent.OperationType,
            Outcome = auditEvent.Outcome.ToString(),
            InputJson = auditEvent.Input.Json,
            OutputJson = auditEvent.Output.Json,
            ErrorClassification = auditEvent.ErrorClassification,
            PreviousHash = previousHash,
            EventHash = eventHash,
        };

    private static AuditEventRecord Map(AuditEventEntity entity) => new(
        entity.EventId,
        entity.Sequence,
        entity.Timestamp,
        entity.InstallationId is null ? null : new InstallationId(entity.InstallationId.Value),
        new ActorId(entity.ActorId),
        new CorrelationId(entity.CorrelationId),
        entity.CausationId is null ? null : new CorrelationId(entity.CausationId),
        entity.OperationType,
        Enum.Parse<AuditOutcome>(entity.Outcome, ignoreCase: false),
        new RedactedData(entity.InputJson),
        new RedactedData(entity.OutputJson),
        entity.ErrorClassification,
        entity.PreviousHash,
        entity.EventHash);
}
