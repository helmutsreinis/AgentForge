using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Search;

namespace AgentForge.Search;

internal static class SearchContractValidator
{
    public static DomainResult<SearchRequest> Normalize(SearchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var query = request.Query.Trim();
        var providerIds = request.ProviderIds
            .Select(item => item.Trim().ToLowerInvariant())
            .Where(item => item.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToImmutableArray();

        if (query.Length is < 1 or > 512 ||
            request.MaximumResults is < 1 or > 50 ||
            providerIds.Length is < 1 or > 8 ||
            !IsIdentifier(request.ScopeId, 256) ||
            !IsIdentifier(request.ActorId, 256) ||
            !IsIdentifier(request.CorrelationId, 128) ||
            request.RequestedAtUtc.Offset != TimeSpan.Zero ||
            request.CacheLifetime < TimeSpan.Zero ||
            request.CacheLifetime > TimeSpan.FromHours(24))
        {
            return Invalid<SearchRequest>("Search request bounds or identity are invalid.");
        }

        return DomainResult.Success(request with
        {
            Query = query,
            ProviderIds = providerIds,
        });
    }

    public static string QueryHash(SearchRequest request) => Hash(
        $"v1\n{request.Query}\n{request.MaximumResults.ToString(CultureInfo.InvariantCulture)}\n" +
        $"{string.Join(',', request.ProviderIds)}\n{request.ScopeId}");

    public static string CitationHash(Uri source, string title, string excerpt, IEnumerable<string> providers) =>
        Hash($"v1\n{source.AbsoluteUri}\n{title}\n{excerpt}\n{string.Join(',', providers)}");

    public static string ResponseHash(string queryHash, IEnumerable<SearchCitation> citations, DateTimeOffset expires) =>
        Hash($"v1\n{queryHash}\n{expires.UtcTicks}\n{string.Join('\n', citations.Select(item => item.EvidenceHash))}");

    public static string Hash(string value) => $"sha256:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant()}";

    public static bool IsSafeHit(SearchProviderHit hit, string expectedProvider)
    {
        return string.Equals(hit.ProviderId, expectedProvider, StringComparison.Ordinal) &&
            hit.ProviderRank is >= 1 and <= 1000 &&
            hit.Source.IsAbsoluteUri &&
            hit.Source.Scheme is "https" or "http" &&
            hit.Title.Length is >= 1 and <= 512 &&
            hit.Snippet.Length <= 4096 &&
            hit.ObservedAtUtc.Offset == TimeSpan.Zero;
    }

    private static bool IsIdentifier(string value, int maximum) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximum &&
        !value.Any(char.IsControl);

    private static DomainResult<T> Invalid<T>(string message) => DomainResult.Fail<T>(
        new DomainFailure(FailureCode.ValidationFailure, message));
}
