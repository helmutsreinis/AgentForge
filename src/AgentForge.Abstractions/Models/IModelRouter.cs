using AgentForge.Domain.Models;
using AgentForge.Domain.Primitives;

namespace AgentForge.Abstractions.Models;

public interface IModelRouter
{
    DomainResult<ModelRouteSelection> SelectRoute(ModelRoutingRequest request);
}
