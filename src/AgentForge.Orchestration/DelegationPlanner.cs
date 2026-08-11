using AgentForge.Abstractions.Orchestration;
using AgentForge.Abstractions.Time;
using AgentForge.Domain.Orchestration;
using AgentForge.Domain.Primitives;

namespace AgentForge.Orchestration;

internal sealed class DelegationPlanner(IClock clock) : IDelegationPlanner
{
    public DomainResult<ChildDelegationGrant> Evaluate(
        ParentDelegationAuthority parent,
        ChildDelegationRequest request) =>
        DelegationAuthorityEvaluator.Evaluate(parent, request, clock.UtcNow);
}
