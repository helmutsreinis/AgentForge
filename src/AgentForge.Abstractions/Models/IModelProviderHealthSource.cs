using AgentForge.Domain.Models;
using AgentForge.Domain.Primitives;

namespace AgentForge.Abstractions.Models;

public interface IModelProviderHealthSource
{
    ValueTask<DomainResult<IReadOnlyList<ModelProviderHealthEvidence>>> ReadAsync(
        CancellationToken cancellationToken);
}
