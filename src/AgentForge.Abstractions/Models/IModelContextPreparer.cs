using AgentForge.Domain.Models;
using AgentForge.Domain.Primitives;

namespace AgentForge.Abstractions.Models;

public interface IModelContextPreparer
{
    DomainResult<PreparedModelContext> Prepare(ModelRequest request);
}
