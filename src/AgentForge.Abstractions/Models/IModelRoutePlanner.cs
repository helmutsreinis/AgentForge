using AgentForge.Domain.Models;
using AgentForge.Domain.Primitives;

namespace AgentForge.Abstractions.Models;

public interface IModelRoutePlanner
{
    Task<DomainResult<ModelRoutePlan>> PlanAsync(
        ModelRoutePlanningRequest request,
        CancellationToken cancellationToken);
}
