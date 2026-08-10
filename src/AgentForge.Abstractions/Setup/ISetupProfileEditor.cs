using AgentForge.Domain.Primitives;
using AgentForge.Domain.Setup;

namespace AgentForge.Abstractions.Setup;

public interface ISetupProfileEditor
{
    Task<DomainResult<ProviderEditPreview>> PreviewProviderAsync(
        PreviewProviderEditRequest request,
        CancellationToken cancellationToken);

    Task<DomainResult<ProviderEditResult>> ApplyProviderAsync(
        ApplyProviderEditRequest request,
        CancellationToken cancellationToken);

    Task<DomainResult<AgentEditPreview>> PreviewAgentAsync(
        PreviewAgentEditRequest request,
        CancellationToken cancellationToken);

    Task<DomainResult<AgentEditResult>> ApplyAgentAsync(
        ApplyAgentEditRequest request,
        CancellationToken cancellationToken);
}
