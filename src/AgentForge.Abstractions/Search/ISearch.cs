using AgentForge.Domain.Primitives;
using AgentForge.Domain.Search;

namespace AgentForge.Abstractions.Search;

public interface ISearchProvider
{
    SearchProviderDescriptor Descriptor { get; }

    Task<SearchProviderResponse> SearchAsync(SearchRequest request, CancellationToken cancellationToken);
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
