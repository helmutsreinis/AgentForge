using AgentForge.Abstractions.Time;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Search;
using AgentForge.Search;
using AgentForge.Security;

namespace AgentForge.IntegrationTests;

public sealed class BraveSearchLiveIntegrationTests
{
    private const string CredentialVariable = "AGENTFORGE_LIVE_BRAVE_SEARCH_API_KEY";

    [BraveSearchLiveFact]
    [Trait("Category", "Live")]
    public async Task Configured_key_returns_bounded_cited_web_results_through_official_adapter()
    {
        var key = global::System.Environment.GetEnvironmentVariable(CredentialVariable);
        Assert.False(string.IsNullOrWhiteSpace(key));
        var clock = new LiveClock();
        using var secrets = new DeterministicSecretStore(clock);
        var stored = await secrets.StoreAsync("brave-live-gate", key.AsMemory(), CancellationToken.None);
        Assert.True(stored.IsSuccess);
        try
        {
            var created = SearchHttpProvider.CreateBrave(
                "brave",
                new SearchHttpProviderOptions(
                    new Uri("https://api.search.brave.com/res/v1/web/search"),
                    stored.Value,
                    null,
                    Timeout: TimeSpan.FromSeconds(15),
                    SafeSearch: SearchSafeSearch.Moderate,
                    SearchLanguage: "en"),
                secrets,
                clock);
            Assert.True(created.IsSuccess, created.Failure?.Message);
            using var provider = created.Value;
            var response = await provider.SearchAsync(new SearchRequest(
                "AgentForge software agent harness",
                3,
                ["brave"],
                "live-gate",
                "live-gate",
                "brave-live-gate",
                clock.UtcNow,
                TimeSpan.Zero), CancellationToken.None);

            Assert.Null(response.Failure);
            Assert.NotEmpty(response.Hits);
            Assert.All(response.Hits, hit =>
            {
                Assert.Equal("brave", hit.ProviderId);
                Assert.True(hit.Source.Scheme is "https" or "http");
                Assert.NotEmpty(hit.Title);
            });
        }
        finally
        {
            _ = await secrets.DeleteAsync(stored.Value, CancellationToken.None);
        }
    }

    private sealed class LiveClock : IClock, IIdentifierGenerator
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

        public Guid NewGuid() => Guid.NewGuid();
    }
}

internal sealed class BraveSearchLiveFactAttribute : FactAttribute
{
    public BraveSearchLiveFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(global::System.Environment.GetEnvironmentVariable(
                "AGENTFORGE_LIVE_BRAVE_SEARCH_API_KEY")))
        {
            Skip = "Set AGENTFORGE_LIVE_BRAVE_SEARCH_API_KEY to run the credential-gated Brave Search live test.";
        }
    }
}
