using AgentForge.Domain.Coding;
using AgentForge.Domain.Primitives;

namespace AgentForge.Abstractions.Coding;

public interface IRepositoryDiscovery
{
    Task<DomainResult<RepositoryProfile>> DiscoverAsync(
        string repositoryRoot,
        CancellationToken cancellationToken);
}

public interface ISemanticNavigator
{
    Task<DomainResult<SemanticResult>> AnalyzeAsync(
        RepositoryProfile repository,
        SemanticQuery query,
        CancellationToken cancellationToken);
}

public interface ICodingWorkspaceManager
{
    Task<DomainResult<CodingWorkspace>> CreateAsync(
        CodingWorkspaceRequest request,
        CancellationToken cancellationToken);

    Task<DomainResult<bool>> RemoveAsync(
        CodingWorkspace workspace,
        CancellationToken cancellationToken);
}
