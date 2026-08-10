using AgentForge.Domain.Primitives;
using AgentForge.Domain.Providers;
using AgentForge.Domain.Setup;

namespace AgentForge.Abstractions.Setup;

public interface ISetupApplicationService
{
    Task<DomainResult<BeginSetupResult>> BeginAsync(
        BeginSetupRequest request,
        CancellationToken cancellationToken);

    Task<DomainResult<ConfigureProviderResult>> ConfigureProviderAsync(
        ConfigureProviderRequest request,
        CancellationToken cancellationToken);
}
