using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.Json;
using AgentForge.Abstractions.Auditing;
using AgentForge.Abstractions.Persistence;
using AgentForge.Abstractions.Search;
using AgentForge.Abstractions.Security;
using AgentForge.Abstractions.Time;
using AgentForge.Domain.Auditing;
using AgentForge.Domain.Persistence;
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
    public async Task Provider_authority_evidence_partitions_research_cache_after_rotation()
    {
        var provider = new DeterministicSearchProvider("brave", [Hit(
            "brave", 1, new Uri("https://example.test/authority"), "Authority", "Pinned result")]);
        var service = new ResearchService([provider], new InMemoryResearchCache(), new FixedClock());
        var firstRequest = Request(["brave"]) with
        {
            ProviderEvidenceHashes = ImmutableDictionary<string, string>.Empty
                .Add("brave", $"sha256:{new string('a', 64)}"),
        };
        var rotatedRequest = firstRequest with
        {
            ProviderEvidenceHashes = firstRequest.ProviderEvidenceHashes
                .SetItem("brave", $"sha256:{new string('b', 64)}"),
        };

        var first = await service.ResearchAsync(firstRequest, CancellationToken.None);
        var rotated = await service.ResearchAsync(rotatedRequest, CancellationToken.None);

        Assert.True(first.IsSuccess && rotated.IsSuccess);
        Assert.NotEqual(first.Value.QueryHash, rotated.Value.QueryHash);
        Assert.False(rotated.Value.IsCacheHit);
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
        if (kind == SearchProviderKind.Brave)
        {
            Assert.Contains("safesearch=moderate", handler.RequestUri.Query, StringComparison.Ordinal);
            Assert.Contains("search_lang=en", handler.RequestUri.Query, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Brave_configuration_creates_and_rotates_os_backed_reference_without_persisting_plaintext()
    {
        var repository = new RecordingProfileRepository();
        var secrets = new RecordingSecretStore();
        var probe = new SuccessfulBraveProbe();
        var audit = new FakeAuditRecorder();
        var service = new BraveSearchProviderConfigurationService(
            repository, probe, secrets, audit, new SuccessfulUnitOfWork(), new FixedClock());
        var installationId = new InstallationId(Guid.NewGuid());
        var candidate = new BraveSearchConfigurationCandidate(true, SearchSafeSearch.Strict, "us", "EN");

        var preview = await service.PreviewAsync(
            installationId, null, candidate, "first-key".AsMemory(),
            new ActorId("operator"), new CorrelationId("brave-create"), CancellationToken.None);
        Assert.True(preview.IsSuccess);
        Assert.Equal("US", preview.Value.Candidate.CountryCode);
        Assert.Equal("en", preview.Value.Candidate.SearchLanguage);
        Assert.True(preview.Value.UsesNewCredential);

        var created = await service.ApplyAsync(preview.Value, "first-key".AsMemory(), CancellationToken.None);
        Assert.True(created.IsSuccess);
        Assert.Equal(0, created.Value.Profile.Version);
        Assert.DoesNotContain("first-key", JsonSerializer.Serialize(created.Value.Profile), StringComparison.Ordinal);
        var originalReference = created.Value.Profile.CredentialReference;
        Assert.Equal(["search.brave.configured"], audit.Operations);

        var rotation = await service.PreviewAsync(
            installationId, 0, candidate with { SafeSearch = SearchSafeSearch.Moderate }, "second-key".AsMemory(),
            new ActorId("operator"), new CorrelationId("brave-rotate"), CancellationToken.None);
        Assert.True(rotation.IsSuccess);
        var rotated = await service.ApplyAsync(rotation.Value, "second-key".AsMemory(), CancellationToken.None);

        Assert.True(rotated.IsSuccess);
        Assert.Equal(1, rotated.Value.Profile.Version);
        Assert.True(rotated.Value.CredentialRotated);
        Assert.NotEqual(originalReference, rotated.Value.Profile.CredentialReference);
        Assert.Contains(originalReference, secrets.Deleted);
        Assert.Equal(4, probe.Credentials.Count);
        Assert.Equal(["first-key", "first-key", "second-key", "second-key"], probe.Credentials);
        Assert.Equal(["search.brave.configured", "search.brave.updated"], audit.Operations);
    }

    [Fact]
    public async Task Brave_configuration_retains_existing_key_and_rejects_stale_or_mismatched_apply()
    {
        var repository = new RecordingProfileRepository();
        var secrets = new RecordingSecretStore();
        var service = new BraveSearchProviderConfigurationService(
            repository, new SuccessfulBraveProbe(), secrets, new FakeAuditRecorder(),
            new SuccessfulUnitOfWork(), new FixedClock());
        var installationId = new InstallationId(Guid.NewGuid());
        var candidate = new BraveSearchConfigurationCandidate(true, SearchSafeSearch.Moderate, "", "en");
        var initial = await service.PreviewAsync(
            installationId, null, candidate, "first-key".AsMemory(),
            new ActorId("operator"), new CorrelationId("create"), CancellationToken.None);
        Assert.True(initial.IsSuccess);
        Assert.True((await service.ApplyAsync(initial.Value, "first-key".AsMemory(), CancellationToken.None)).IsSuccess);

        var retained = await service.PreviewAsync(
            installationId, 0, candidate with { SafeSearch = SearchSafeSearch.Strict }, ReadOnlyMemory<char>.Empty,
            new ActorId("operator"), new CorrelationId("retain"), CancellationToken.None);
        Assert.True(retained.IsSuccess);
        Assert.False(retained.Value.UsesNewCredential);
        var mismatched = await service.ApplyAsync(retained.Value, "unexpected-key".AsMemory(), CancellationToken.None);
        Assert.Equal(FailureCode.PolicyDenied, mismatched.Failure?.Code);

        repository.Current = repository.Current! with { Version = 2 };
        var stale = await service.ApplyAsync(retained.Value, ReadOnlyMemory<char>.Empty, CancellationToken.None);
        Assert.Equal(FailureCode.ConcurrencyConflict, stale.Failure?.Code);
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
        private readonly Dictionary<SecretReference, char[]> _secrets = [];
        private int _nextKey;

        public string StoreName => "fake";

        public int MaterializeCount { get; private set; }

        public List<SecretReference> Deleted { get; } = [];

        public SecretStoreCapability GetCapability() => new(StoreName, true, null);

        public Task<DomainResult<SecretReference>> StoreAsync(string logicalName, ReadOnlyMemory<char> secret, CancellationToken cancellationToken)
        {
            var reference = new SecretReference(StoreName, $"key-{++_nextKey}");
            _secrets[reference] = secret.ToArray();
            return Task.FromResult(DomainResult.Success(reference));
        }

        public Task<DomainResult<SecretLease>> MaterializeAsync(SecretReference secretReference, CancellationToken cancellationToken)
        {
            MaterializeCount++;
            var credential = _secrets.TryGetValue(secretReference, out var stored)
                ? stored.ToArray()
                : "super-secret".ToCharArray();
            return Task.FromResult(DomainResult.Success(new SecretLease(credential)));
        }

        public Task<DomainResult<bool>> DeleteAsync(SecretReference secretReference, CancellationToken cancellationToken)
        {
            Deleted.Add(secretReference);
            if (_secrets.Remove(secretReference, out var value)) Array.Clear(value);
            return Task.FromResult(DomainResult.Success(true));
        }
    }

    private sealed class SuccessfulBraveProbe : IBraveSearchConnectivityProbe
    {
        public List<string> Credentials { get; } = [];

        public Task<DomainResult<BraveSearchProbeEvidence>> ProbeAsync(
            ReadOnlyMemory<char> credential,
            BraveSearchConfigurationCandidate candidate,
            CancellationToken cancellationToken)
        {
            Credentials.Add(new string(credential.Span));
            return Task.FromResult(DomainResult.Success(new BraveSearchProbeEvidence(
                1, TimeSpan.FromMilliseconds(25), $"sha256:{new string('a', 64)}")));
        }
    }

    private sealed class RecordingProfileRepository : ISearchProviderProfileRepository
    {
        public SearchProviderProfile? Current { get; set; }

        public ValueTask<SearchProviderProfile?> FindAsync(
            InstallationId installationId,
            string providerId,
            CancellationToken cancellationToken) => ValueTask.FromResult(
                Current?.InstallationId == installationId && Current.Id == providerId ? Current : null);

        public Task<IReadOnlyList<SearchProviderProfile>> ListAsync(
            InstallationId installationId,
            CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<SearchProviderProfile>>(
                Current?.InstallationId == installationId ? [Current] : []);

        public ValueTask AddAsync(SearchProviderProfile profile, CancellationToken cancellationToken)
        {
            Current = profile;
            return ValueTask.CompletedTask;
        }

        public ValueTask UpdateAsync(
            SearchProviderProfile profile,
            long expectedVersion,
            CancellationToken cancellationToken)
        {
            Assert.Equal(expectedVersion, Current?.Version);
            Current = profile;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class SuccessfulUnitOfWork : IUnitOfWork
    {
        public Task<CommitResult> CommitAsync(CancellationToken cancellationToken) =>
            Task.FromResult(CommitResult.Success(2));
    }

    private sealed class FakeAuditRecorder : IAuditRecorder
    {
        public List<string> Operations { get; } = [];

        public Task<AuditRecordResult> RecordAsync(AuditRecordRequest request, CancellationToken cancellationToken)
        {
            Operations.Add(request.OperationType);
            return Task.FromResult(new AuditRecordResult(new AuditEventRecord(
                Guid.NewGuid(), Operations.Count, Now, request.InstallationId, request.ActorId,
                request.CorrelationId, request.CausationId, request.OperationType, request.Outcome,
                RedactedData.Empty, RedactedData.Empty, null, new string('0', 64), new string('1', 64)), 0, 0));
        }
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
