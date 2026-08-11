using System.Collections.Immutable;
using System.Net;
using System.Text;
using AgentForge.Abstractions.Search;
using AgentForge.Abstractions.Security;
using AgentForge.Abstractions.Time;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Search;
using AgentForge.Domain.Security;
using AgentForge.Search;

namespace AgentForge.UnitTests;

public sealed class SearchServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 12, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Research_deduplicates_fuses_ranks_preserves_citations_and_caches()
    {
        var shared = new Uri("https://example.test/a#fragment");
        var brave = new DeterministicSearchProvider("brave", [
            Hit("brave", 1, shared, "A", "Brave excerpt"),
            Hit("brave", 2, new Uri("https://example.test/b"), "B", "Second")]);
        var google = new DeterministicSearchProvider("google", [
            Hit("google", 1, new Uri("https://example.test/a"), "A duplicate", "Google excerpt")]);
        var cache = new InMemoryResearchCache();
        var service = new ResearchService([brave, google], cache, new FixedClock());
        var request = Request(["google", "brave"]);

        var first = await service.ResearchAsync(request, CancellationToken.None);
        var second = await service.ResearchAsync(request, CancellationToken.None);

        Assert.True(first.IsSuccess);
        Assert.Equal(2, first.Value.Citations.Length);
        Assert.Equal(["brave", "google"], first.Value.Citations[0].ProviderIds.ToArray());
        Assert.StartsWith("cite-", first.Value.Citations[0].CitationId, StringComparison.Ordinal);
        Assert.False(first.Value.IsCacheHit);
        Assert.True(second.IsSuccess);
        Assert.True(second.Value.IsCacheHit);
        Assert.Equal(first.Value.EvidenceHash, second.Value.EvidenceHash);
    }

    [Fact]
    public async Task Throttled_provider_does_not_discard_other_cited_results()
    {
        var throttled = new DeterministicSearchProvider("brave", [], new SearchProviderFailure(
            "brave", SearchFailureKind.Throttled, true));
        var local = new DeterministicSearchProvider("local", [Hit(
            "local", 1, new Uri("https://docs.example.test/answer"), "Answer", "Sourced answer")]);
        var service = new ResearchService([throttled, local], new InMemoryResearchCache(), new FixedClock());

        var result = await service.ResearchAsync(Request(["brave", "local"]), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Single(result.Value.Citations);
        Assert.Equal(SearchFailureKind.Throttled, Assert.Single(result.Value.ProviderFailures).Kind);
    }

    [Fact]
    public async Task Unknown_provider_and_all_provider_outage_fail_typed()
    {
        var service = new ResearchService([], new InMemoryResearchCache(), new FixedClock());
        var unknown = await service.ResearchAsync(Request(["missing"]), CancellationToken.None);
        Assert.Equal(FailureCode.UnsupportedCapability, unknown.Failure?.Code);

        var unavailable = new DeterministicSearchProvider("brave", [], new SearchProviderFailure(
            "brave", SearchFailureKind.Unavailable, true));
        service = new ResearchService([unavailable], new InMemoryResearchCache(), new FixedClock());
        var outage = await service.ResearchAsync(Request(["brave"]), CancellationToken.None);
        Assert.Equal(FailureCode.RecoverableExternalFailure, outage.Failure?.Code);
        Assert.True(outage.Failure?.IsRetryable);
    }

    [Theory]
    [InlineData(SearchProviderKind.Brave, "https://api.search.brave.com/res/v1/web/search", "{\"web\":{\"results\":[{\"url\":\"https://example.test\",\"title\":\"Title\",\"description\":\"Excerpt\"}]}}", "X-Subscription-Token")]
    [InlineData(SearchProviderKind.GoogleCustomSearch, "https://customsearch.googleapis.com/customsearch/v1", "{\"items\":[{\"link\":\"https://example.test\",\"title\":\"Title\",\"snippet\":\"Excerpt\"}]}", "X-Goog-Api-Key")]
    public async Task Official_http_adapters_materialize_header_for_one_bounded_invocation(
        SearchProviderKind kind,
        string endpoint,
        string payload,
        string expectedHeader)
    {
        var secretStore = new RecordingSecretStore();
        var handler = new RecordingHandler(payload, expectedHeader);
        var created = SearchHttpProvider.CreateForTesting(
            kind == SearchProviderKind.Brave ? "brave" : "google",
            kind,
            new SearchHttpProviderOptions(
                new Uri(endpoint),
                new SecretReference("fake", "search-key"),
                kind == SearchProviderKind.GoogleCustomSearch ? "engine-id" : null),
            secretStore,
            new FixedClock(),
            handler);
        Assert.True(created.IsSuccess);
        using var provider = created.Value;

        var response = await provider.SearchAsync(Request([provider.Descriptor.Id]), CancellationToken.None);

        Assert.Null(response.Failure);
        Assert.Single(response.Hits);
        Assert.Equal(1, secretStore.MaterializeCount);
        Assert.True(handler.SawCredential);
        Assert.DoesNotContain("super-secret", handler.RequestUri!.AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public void Official_adapter_rejects_noncanonical_endpoint()
    {
        var handler = new RecordingHandler("{}", "X-Subscription-Token");
        var created = SearchHttpProvider.CreateForTesting(
            "brave",
            SearchProviderKind.Brave,
            new SearchHttpProviderOptions(
                new Uri("https://evil.example.test/res/v1/web/search"),
                new SecretReference("fake", "key"),
                null),
            new RecordingSecretStore(),
            new FixedClock(),
            handler);

        Assert.Equal(FailureCode.ValidationFailure, created.Failure?.Code);
        Assert.True(handler.WasDisposed);
    }

    private static SearchRequest Request(ImmutableArray<string> providers) => new(
        "agent harness security",
        10,
        providers,
        "scope-1",
        "actor-1",
        "corr-1",
        Now,
        TimeSpan.FromMinutes(10));

    private static SearchProviderHit Hit(string provider, int rank, Uri source, string title, string excerpt) =>
        new(provider, rank, source, title, excerpt, Now);

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    private sealed class RecordingSecretStore : ISecretStore
    {
        public string StoreName => "fake";

        public int MaterializeCount { get; private set; }

        public SecretStoreCapability GetCapability() => new(StoreName, true, null);

        public Task<DomainResult<SecretReference>> StoreAsync(string logicalName, ReadOnlyMemory<char> secret, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<DomainResult<SecretLease>> MaterializeAsync(SecretReference secretReference, CancellationToken cancellationToken)
        {
            MaterializeCount++;
            return Task.FromResult(DomainResult.Success(new SecretLease("super-secret".ToCharArray())));
        }

        public Task<DomainResult<bool>> DeleteAsync(SecretReference secretReference, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingHandler(string payload, string expectedHeader) : HttpMessageHandler
    {
        public bool SawCredential { get; private set; }

        public bool WasDisposed { get; private set; }

        public Uri? RequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            SawCredential = request.Headers.TryGetValues(expectedHeader, out var values) && values.Single() == "super-secret";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            });
        }

        protected override void Dispose(bool disposing)
        {
            WasDisposed = true;
            base.Dispose(disposing);
        }
    }
}
