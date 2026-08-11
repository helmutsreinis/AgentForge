using AgentForge.Domain.Models;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Providers;

namespace AgentForge.Abstractions.Models;

public interface IModelProvider
{
    ModelProviderDescriptor Descriptor { get; }

    IAsyncEnumerable<ModelStreamEvent> StreamAsync(
        ModelRequest request,
        CancellationToken cancellationToken);
}

public interface IModelProviderCatalog
{
    DomainResult<IModelProvider> Resolve(ProviderProfileId profileId);

    IReadOnlyList<ModelProviderDescriptor> List();
}
