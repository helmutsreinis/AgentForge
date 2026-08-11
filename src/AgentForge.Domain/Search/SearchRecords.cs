using System.Collections.Immutable;

namespace AgentForge.Domain.Search;

public enum SearchProviderKind
{
    Deterministic,
    Local,
    Brave,
    GoogleCustomSearch,
}

public enum SearchFailureKind
{
    Unavailable,
    QuotaExceeded,
    Throttled,
    InvalidResponse,
}

public sealed record SearchProviderDescriptor(
    string Id,
    SearchProviderKind Kind,
    bool RequiresCredential,
    int Priority,
    string EvidenceHash);

public sealed record SearchRequest(
    string Query,
    int MaximumResults,
    ImmutableArray<string> ProviderIds,
    string ScopeId,
    string ActorId,
    string CorrelationId,
    DateTimeOffset RequestedAtUtc,
    TimeSpan CacheLifetime);

public sealed record SearchProviderHit(
    string ProviderId,
    int ProviderRank,
    Uri Source,
    string Title,
    string Snippet,
    DateTimeOffset ObservedAtUtc);

public sealed record SearchProviderFailure(
    string ProviderId,
    SearchFailureKind Kind,
    bool IsRetryable);

public sealed record SearchProviderResponse(
    ImmutableArray<SearchProviderHit> Hits,
    SearchProviderFailure? Failure);

public sealed record SearchCitation(
    string CitationId,
    Uri Source,
    string Title,
    string Excerpt,
    ImmutableArray<string> ProviderIds,
    double ReciprocalRankScore,
    string EvidenceHash);

public sealed record ResearchResponse(
    string QueryHash,
    ImmutableArray<SearchCitation> Citations,
    ImmutableArray<SearchProviderFailure> ProviderFailures,
    bool IsCacheHit,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    string EvidenceHash);
