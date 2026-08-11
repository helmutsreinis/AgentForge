using System.Collections.Immutable;
using AgentForge.Abstractions.Search;
using AgentForge.Domain.Search;

namespace AgentForge.Search;

public sealed class DeterministicSearchProvider : ISearchProvider
{
    private readonly ImmutableArray<SearchProviderHit> _hits;
    private readonly SearchProviderFailure? _failure;

    public DeterministicSearchProvider(
        string id,
        IEnumerable<SearchProviderHit> hits,
        SearchProviderFailure? failure = null,
        SearchProviderKind kind = SearchProviderKind.Deterministic)
    {
        ArgumentNullException.ThrowIfNull(hits);
        var normalizedId = id.Trim().ToLowerInvariant();
        _hits = hits.Select(item => item with { ProviderId = normalizedId }).ToImmutableArray();
        _failure = failure is null ? null : failure with { ProviderId = normalizedId };
        Descriptor = new SearchProviderDescriptor(
            normalizedId,
            kind,
            false,
            100,
            SearchContractValidator.Hash($"deterministic:{normalizedId}"));
    }

    public SearchProviderDescriptor Descriptor { get; }

    public Task<SearchProviderResponse> SearchAsync(SearchRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new SearchProviderResponse(_hits, _failure));
    }
}
