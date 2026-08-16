using AgentForge.Domain.Primitives;
using AgentForge.Domain.Search;

namespace AgentForge.Abstractions.Search;

public interface ISearchProvider
{
    SearchProviderDescriptor Descriptor { get; }

    Task<SearchProviderResponse> SearchAsync(SearchRequest request, CancellationToken cancellationToken);
}

public interface ISearchProviderProfileRepository
{
    ValueTask<SearchProviderProfile?> FindAsync(
        InstallationId installationId,
        string providerId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<SearchProviderProfile>> ListAsync(
        InstallationId installationId,
        CancellationToken cancellationToken);

    ValueTask AddAsync(SearchProviderProfile profile, CancellationToken cancellationToken);

    ValueTask UpdateAsync(
        SearchProviderProfile profile,
        long expectedVersion,
        CancellationToken cancellationToken);
}

public interface IBraveSearchConnectivityProbe
{
    Task<DomainResult<BraveSearchProbeEvidence>> ProbeAsync(
        ReadOnlyMemory<char> credential,
        BraveSearchConfigurationCandidate candidate,
        CancellationToken cancellationToken);
}

public interface IBraveSearchProviderConfigurationService
{
    ValueTask<SearchProviderProfile?> FindAsync(
        InstallationId installationId,
        CancellationToken cancellationToken);

    Task<DomainResult<BraveSearchConfigurationPreview>> PreviewAsync(
        InstallationId installationId,
        long? expectedVersion,
        BraveSearchConfigurationCandidate candidate,
        ReadOnlyMemory<char> credential,
        ActorId actorId,
        CorrelationId correlationId,
        CancellationToken cancellationToken);

    Task<DomainResult<BraveSearchConfigurationResult>> ApplyAsync(
        BraveSearchConfigurationPreview preview,
        ReadOnlyMemory<char> credential,
        CancellationToken cancellationToken);
}

public interface IResearchCache
{
    Task<ResearchResponse?> ReadAsync(string queryHash, DateTimeOffset nowUtc, CancellationToken cancellationToken);

    Task WriteAsync(ResearchResponse response, CancellationToken cancellationToken);
}

public interface IResearchService
{
    Task<DomainResult<ResearchResponse>> ResearchAsync(SearchRequest request, CancellationToken cancellationToken);
}
