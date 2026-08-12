using AgentForge.Domain.Persistence;
using AgentForge.Domain.Primitives;

namespace AgentForge.Abstractions.Persistence;

public interface IEventOutbox
{
    Task<DomainResult<OutboxEvent>> EnqueueAsync(
        OutboxEvent outboxEvent,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<OutboxEvent>> ReadPendingAsync(
        int maximumCount,
        CancellationToken cancellationToken);

    Task<DomainResult<OutboxEvent>> MarkProcessedAsync(
        Guid id,
        long expectedVersion,
        DateTimeOffset processedAt,
        CancellationToken cancellationToken);
}
