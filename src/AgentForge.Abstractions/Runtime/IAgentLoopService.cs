using AgentForge.Domain.Agents;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Runtime;

namespace AgentForge.Abstractions.Runtime;

public sealed record AgentLoopRunRequest(
    AgentLoopId LoopId,
    InstallationId InstallationId,
    AgentIdentityId AgentId,
    long AgentVersion,
    AgentLoopBudget Budget,
    string InitialStateHash,
    ActorId ActorId,
    string IdempotencyKey,
    CorrelationId CorrelationId,
    CorrelationId? CausationId = null);

public sealed record AgentLoopRunResult(AgentLoopSnapshot Snapshot, bool WasResumed);

public interface IAgentLoopStepExecutor
{
    Task<DomainResult<AgentLoopStepResult>> ExecuteAsync(
        AgentLoopSnapshot snapshot,
        CancellationToken cancellationToken);
}

public interface IAgentLoopService
{
    Task<DomainResult<AgentLoopRunResult>> RunAsync(
        AgentLoopRunRequest request,
        CancellationToken cancellationToken);
}

public interface IRunSnapshotStore
{
    ValueTask AppendAsync(AgentLoopSnapshot snapshot, CancellationToken cancellationToken);

    ValueTask<AgentLoopSnapshot?> FindLatestAsync(
        AgentLoopId loopId,
        CancellationToken cancellationToken);

    ValueTask<AgentLoopSnapshot?> FindByIdempotencyKeyAsync(
        InstallationId installationId,
        string idempotencyKey,
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<AgentLoopSnapshot>> ListAsync(
        AgentLoopId loopId,
        CancellationToken cancellationToken);
}
