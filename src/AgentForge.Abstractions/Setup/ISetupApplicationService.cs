using AgentForge.Domain.Agents;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Providers;
using AgentForge.Domain.Security;
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

    Task<DomainResult<ConfigureProviderResult>> ConfigureProviderCredentialAsync(
        ConfigureProviderCredentialRequest request,
        CancellationToken cancellationToken);

    Task<DomainResult<EffectiveAgentDefinition>> PreviewAgentAsync(
        PreviewAgentRequest request,
        CancellationToken cancellationToken);

    Task<DomainResult<CreateAgentResult>> CreateAgentAsync(
        CreateAgentRequest request,
        CancellationToken cancellationToken);

    Task<DomainResult<SetupCompletionReport>> CompleteAsync(
        CompleteSetupRequest request,
        CancellationToken cancellationToken);
}
