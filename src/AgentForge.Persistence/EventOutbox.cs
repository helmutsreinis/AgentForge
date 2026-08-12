using AgentForge.Abstractions.Persistence;
using AgentForge.Domain.Persistence;
using AgentForge.Domain.Primitives;
using AgentForge.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace AgentForge.Persistence;

internal sealed class EventOutbox(AgentForgeDbContext dbContext) : IEventOutbox
{
    public async Task<DomainResult<OutboxEvent>> EnqueueAsync(
        OutboxEvent outboxEvent,
        CancellationToken cancellationToken)
    {
        var validation = OutboxEventValidator.Validate(outboxEvent);
        if (!validation.IsSuccess || outboxEvent.ProcessedAt is not null || outboxEvent.Attempts != 0 ||
            outboxEvent.Version != 0)
            return DomainResult.Fail<OutboxEvent>(validation.Failure ?? new DomainFailure(
                FailureCode.ValidationFailure, "New outbox event must be pending at version zero."));
        if (await dbContext.OutboxMessages.AnyAsync(item => item.Id == outboxEvent.Id, cancellationToken))
            return DomainResult.Fail<OutboxEvent>(new DomainFailure(
                FailureCode.ConcurrencyConflict, "Outbox event identity already exists.", true));
        dbContext.OutboxMessages.Add(ToEntity(outboxEvent));
        return DomainResult.Success(outboxEvent);
    }

    public async Task<IReadOnlyList<OutboxEvent>> ReadPendingAsync(
        int maximumCount,
        CancellationToken cancellationToken)
    {
        if (maximumCount is < 1 or > 1024) throw new ArgumentOutOfRangeException(nameof(maximumCount));
        return await dbContext.OutboxMessages.AsNoTracking()
            .Where(item => item.ProcessedAt == null)
            .OrderBy(item => item.OccurredAtUtcTicks)
            .ThenBy(item => item.Id)
            .Take(maximumCount)
            .Select(item => ToDomain(item))
            .ToArrayAsync(cancellationToken);
    }

    public async Task<DomainResult<OutboxEvent>> MarkProcessedAsync(
        Guid id,
        long expectedVersion,
        DateTimeOffset processedAt,
        CancellationToken cancellationToken)
    {
        if (id == Guid.Empty || expectedVersion < 0 || processedAt == default)
            return Invalid("Outbox completion identity, version, and time are required.");
        var entity = await dbContext.OutboxMessages.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (entity is null) return Invalid("Outbox event does not exist.");
        if (entity.ProcessedAt is not null || entity.Version != expectedVersion || processedAt < entity.OccurredAt)
            return DomainResult.Fail<OutboxEvent>(new DomainFailure(
                FailureCode.ConcurrencyConflict, "Outbox event is terminal, stale, or has invalid time.", true));
        entity.ProcessedAt = processedAt;
        entity.Attempts = checked(entity.Attempts + 1);
        entity.Version = checked(entity.Version + 1);
        return DomainResult.Success(ToDomain(entity));
    }

    private static OutboxMessageEntity ToEntity(OutboxEvent value) => new()
    {
        Id = value.Id,
        OccurredAt = value.OccurredAt,
        OccurredAtUtcTicks = value.OccurredAt.UtcTicks,
        MessageType = value.MessageType,
        PayloadJson = value.PayloadJson,
        ProcessedAt = value.ProcessedAt,
        Attempts = value.Attempts,
        Version = value.Version,
    };

    private static OutboxEvent ToDomain(OutboxMessageEntity value) => new(
        value.Id, value.OccurredAt, value.MessageType, value.PayloadJson,
        value.ProcessedAt, value.Attempts, value.Version);

    private static DomainResult<OutboxEvent> Invalid(string message) =>
        DomainResult.Fail<OutboxEvent>(new DomainFailure(FailureCode.ValidationFailure, message));
}
