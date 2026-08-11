using System.Collections.Immutable;
using AgentForge.Abstractions.Search;
using AgentForge.Abstractions.Time;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Search;

namespace AgentForge.Search;

public sealed class ResearchService(
    IEnumerable<ISearchProvider> providers,
    IResearchCache cache,
    IClock clock) : IResearchService
{
    private readonly ImmutableDictionary<string, ISearchProvider> _providers = providers
        .ToImmutableDictionary(item => item.Descriptor.Id, StringComparer.Ordinal);

    public async Task<DomainResult<ResearchResponse>> ResearchAsync(
        SearchRequest request,
        CancellationToken cancellationToken)
    {
        var normalized = SearchContractValidator.Normalize(request);
        if (!normalized.IsSuccess)
        {
            return DomainResult.Fail<ResearchResponse>(normalized.Failure!);
        }

        var queryHash = SearchContractValidator.QueryHash(normalized.Value);
        var cached = await cache.ReadAsync(queryHash, clock.UtcNow, cancellationToken);
        if (cached is not null)
        {
            return DomainResult.Success(cached);
        }

        var selected = new List<ISearchProvider>(normalized.Value.ProviderIds.Length);
        foreach (var id in normalized.Value.ProviderIds)
        {
            if (!_providers.TryGetValue(id, out var provider))
            {
                return DomainResult.Fail<ResearchResponse>(new DomainFailure(
                    FailureCode.UnsupportedCapability,
                    "A selected search provider is unavailable."));
            }

            selected.Add(provider);
        }

        var responses = await Task.WhenAll(selected.Select(async provider =>
        {
            try
            {
                return await provider.SearchAsync(normalized.Value, cancellationToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new SearchProviderResponse([], new SearchProviderFailure(
                    provider.Descriptor.Id,
                    SearchFailureKind.Unavailable,
                    true));
            }
            catch (HttpRequestException)
            {
                return new SearchProviderResponse([], new SearchProviderFailure(
                    provider.Descriptor.Id,
                    SearchFailureKind.Unavailable,
                    true));
            }
        }));

        cancellationToken.ThrowIfCancellationRequested();
        var failures = responses
            .Where(item => item.Failure is not null)
            .Select(item => item.Failure!)
            .OrderBy(item => item.ProviderId, StringComparer.Ordinal)
            .ToImmutableArray();
        var safeHits = responses
            .SelectMany((response, index) => response.Hits.Select(hit => (Hit: hit, Provider: selected[index])))
            .Where(item => SearchContractValidator.IsSafeHit(item.Hit, item.Provider.Descriptor.Id))
            .ToArray();

        if (safeHits.Length == 0)
        {
            return DomainResult.Fail<ResearchResponse>(new DomainFailure(
                FailureCode.RecoverableExternalFailure,
                "No search provider returned safe results.",
                failures.Any(item => item.IsRetryable)));
        }

        const double rankConstant = 60d;
        var citations = safeHits
            .GroupBy(item => CanonicalUri(item.Hit.Source), StringComparer.Ordinal)
            .Select(group =>
            {
                var ordered = group.OrderBy(item => item.Hit.ProviderRank).ThenBy(item => item.Provider.Descriptor.Id, StringComparer.Ordinal).ToArray();
                var first = ordered[0].Hit;
                var providerIds = ordered.Select(item => item.Provider.Descriptor.Id).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToImmutableArray();
                var score = ordered.Sum(item => 1d / (rankConstant + item.Hit.ProviderRank));
                var title = Bound(first.Title, 512);
                var excerpt = Bound(first.Snippet, 1024);
                var evidence = SearchContractValidator.CitationHash(first.Source, title, excerpt, providerIds);
                return new SearchCitation(
                    $"cite-{evidence[7..23]}",
                    first.Source,
                    title,
                    excerpt,
                    providerIds,
                    score,
                    evidence);
            })
            .OrderByDescending(item => item.ReciprocalRankScore)
            .ThenBy(item => item.Source.AbsoluteUri, StringComparer.Ordinal)
            .Take(normalized.Value.MaximumResults)
            .ToImmutableArray();
        var createdAt = clock.UtcNow;
        var expiresAt = createdAt + normalized.Value.CacheLifetime;
        var response = new ResearchResponse(
            queryHash,
            citations,
            failures,
            false,
            createdAt,
            expiresAt,
            SearchContractValidator.ResponseHash(queryHash, citations, expiresAt));
        if (normalized.Value.CacheLifetime > TimeSpan.Zero)
        {
            await cache.WriteAsync(response, cancellationToken);
        }

        return DomainResult.Success(response);
    }

    private static string CanonicalUri(Uri value)
    {
        var builder = new UriBuilder(value) { Fragment = string.Empty };
        return builder.Uri.AbsoluteUri;
    }

    private static string Bound(string value, int maximum)
    {
        var printable = new string(value.Where(character => !char.IsControl(character) || character is '\r' or '\n' or '\t').ToArray()).Trim();
        return printable.Length <= maximum ? printable : printable[..maximum];
    }
}
