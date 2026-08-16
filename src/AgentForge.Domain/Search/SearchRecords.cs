using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Security;

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

public enum SearchSafeSearch
{
    Off,
    Moderate,
    Strict,
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
    TimeSpan CacheLifetime)
{
    public ImmutableDictionary<string, string> ProviderEvidenceHashes { get; init; } =
        ImmutableDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal);
}

public sealed record SearchProviderProfile(
    InstallationId InstallationId,
    string Id,
    SearchProviderKind Kind,
    Uri Endpoint,
    SecretReference CredentialReference,
    bool IsEnabled,
    SearchSafeSearch SafeSearch,
    string CountryCode,
    string SearchLanguage,
    long Version,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    ActorId ActorId,
    CorrelationId CorrelationId)
{
    public string EvidenceHash => $"sha256:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
        $"v1\n{InstallationId.Value:D}\n{Id}\n{Kind}\n{Endpoint.AbsoluteUri}\n{CredentialReference.Store}\n" +
        $"{CredentialReference.Key}\n{IsEnabled}\n{SafeSearch}\n{CountryCode}\n{SearchLanguage}\n{Version}")))}";
}

public sealed record BraveSearchConfigurationCandidate(
    bool IsEnabled,
    SearchSafeSearch SafeSearch,
    string CountryCode,
    string SearchLanguage);

public sealed record BraveSearchProbeEvidence(
    int ResultCount,
    TimeSpan Duration,
    string EvidenceHash);

public sealed record BraveSearchConfigurationPreview(
    InstallationId InstallationId,
    long? ExpectedVersion,
    BraveSearchConfigurationCandidate Candidate,
    bool UsesNewCredential,
    string CredentialFingerprint,
    string RequestHash,
    BraveSearchProbeEvidence? Probe,
    ActorId ActorId,
    CorrelationId CorrelationId);

public sealed record BraveSearchConfigurationResult(
    SearchProviderProfile Profile,
    string RequestHash,
    bool CredentialRotated);

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
