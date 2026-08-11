using AgentForge.Domain.Orchestration;
using AgentForge.Domain.Primitives;

namespace AgentForge.Abstractions.Orchestration;

public sealed record TaskTransitionResult(OrchestrationTaskSnapshot Snapshot, bool WasReplay = false);

public sealed record TaskLeaseGrant(
    OrchestrationTaskSnapshot Snapshot,
    TaskNodeId NodeId,
    string LeaseToken,
    DateTimeOffset ExpiresAt);

public interface ITaskSnapshotStore
{
    ValueTask AppendAsync(OrchestrationTaskSnapshot snapshot, CancellationToken cancellationToken);

    ValueTask<OrchestrationTaskSnapshot?> FindLatestAsync(
        OrchestrationTaskId taskId,
        CancellationToken cancellationToken);

    ValueTask<OrchestrationTaskSnapshot?> FindByIdempotencyKeyAsync(
        InstallationId installationId,
        string idempotencyKey,
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<OrchestrationTaskSnapshot>> ListAsync(
        OrchestrationTaskId taskId,
        CancellationToken cancellationToken);
}

public interface ITaskOrchestrator
{
    Task<DomainResult<TaskTransitionResult>> CreateAsync(
        OrchestrationTaskDefinition definition,
        ActorId actorId,
        string idempotencyKey,
        CorrelationId correlationId,
        CorrelationId? causationId,
        CancellationToken cancellationToken);

    Task<DomainResult<TaskLeaseGrant>> ClaimAsync(
        OrchestrationTaskId taskId,
        long expectedVersion,
        TaskNodeId nodeId,
        string owner,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken);

    Task<DomainResult<TaskTransitionResult>> HeartbeatAsync(
        OrchestrationTaskId taskId,
        long expectedVersion,
        TaskNodeId nodeId,
        string owner,
        string leaseToken,
        TimeSpan extension,
        CancellationToken cancellationToken);

    Task<DomainResult<TaskTransitionResult>> CompleteAsync(
        OrchestrationTaskId taskId,
        long expectedVersion,
        TaskNodeId nodeId,
        string owner,
        string leaseToken,
        string evidenceHash,
        CancellationToken cancellationToken);

    Task<DomainResult<TaskTransitionResult>> FailAsync(
        OrchestrationTaskId taskId,
        long expectedVersion,
        TaskNodeId nodeId,
        string owner,
        string leaseToken,
        string evidenceHash,
        FailureCode failureCode,
        bool retryable,
        CancellationToken cancellationToken);

    Task<DomainResult<TaskTransitionResult>> RecoverExpiredAsync(
        OrchestrationTaskId taskId,
        long expectedVersion,
        CancellationToken cancellationToken);

    Task<DomainResult<TaskTransitionResult>> CancelAsync(
        OrchestrationTaskId taskId,
        long expectedVersion,
        CancellationToken cancellationToken);
}
