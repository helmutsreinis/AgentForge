using AgentForge.Domain.Primitives;
using AgentForge.Domain.Setup;

namespace AgentForge.Abstractions.Setup;

public interface ISetupProfileEditor
{
    Task<DomainResult<ProviderCreatePreview>> PreviewProviderCreateAsync(
        PreviewProviderCreateRequest request,
        CancellationToken cancellationToken);

    Task<DomainResult<ProviderCreateResult>> CreateProviderAsync(
        ApplyProviderCreateRequest request,
        CancellationToken cancellationToken);

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

    Task<DomainResult<AgentCreatePreview>> PreviewAgentCreateAsync(
        PreviewAgentCreateRequest request,
        CancellationToken cancellationToken);

    Task<DomainResult<AgentCreateResult>> CreateAgentAsync(
        ApplyAgentCreateRequest request,
        CancellationToken cancellationToken);
}
