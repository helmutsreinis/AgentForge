using AgentForge.Domain.Orchestration;
using AgentForge.Domain.Primitives;

namespace AgentForge.Abstractions.Orchestration;

public interface IDelegationPlanner
{
    DomainResult<ChildDelegationGrant> Evaluate(
        ParentDelegationAuthority parent,
        ChildDelegationRequest request);
}

public sealed record DelegationResult(ChildDelegationGrant Grant, bool WasReplay);

public interface IDelegationGrantStore
{
    ValueTask AddAsync(ChildDelegationGrant grant, ActorId actorId, CancellationToken cancellationToken);

    ValueTask<ChildDelegationGrant?> FindAsync(
        ChildDelegationId delegationId,
        CancellationToken cancellationToken);
}

public interface IDelegationService
{
    Task<DomainResult<DelegationResult>> CreateAsync(
        ParentDelegationAuthority parent,
        ChildDelegationRequest request,
        ActorId actorId,
        CancellationToken cancellationToken);
}
