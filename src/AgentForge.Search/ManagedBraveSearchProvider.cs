using AgentForge.Abstractions.Installations;
using AgentForge.Abstractions.Search;
using AgentForge.Abstractions.Security;
using AgentForge.Abstractions.Time;
using AgentForge.Domain.Search;

namespace AgentForge.Search;

internal sealed class ManagedBraveSearchProvider(
    ISearchProviderProfileRepository profiles,
    IInstallationStateReader installationState,
    ISecretStore secretStore,
    IClock clock) : ISearchProvider
{
    private static readonly Uri Endpoint = new("https://api.search.brave.com/res/v1/web/search");

    public SearchProviderDescriptor Descriptor { get; } = new(
        "brave",
        SearchProviderKind.Brave,
        true,
        10,
        SearchContractValidator.Hash("managed-brave-search-provider-v1"));

    public async Task<SearchProviderResponse> SearchAsync(
        SearchRequest request,
        CancellationToken cancellationToken)
    {
        var installation = await installationState.ReadAsync(cancellationToken);
        var profile = await profiles.FindAsync(installation.Id, Descriptor.Id, cancellationToken);
        if (profile is null || !profile.IsEnabled || profile.Kind != SearchProviderKind.Brave ||
            profile.Endpoint != Endpoint ||
            !request.ProviderEvidenceHashes.TryGetValue(Descriptor.Id, out var expectedEvidence) ||
            !string.Equals(expectedEvidence, profile.EvidenceHash, StringComparison.Ordinal))
        {
            return Unavailable(false);
        }

        var created = SearchHttpProvider.CreateBrave(Descriptor.Id, new SearchHttpProviderOptions(
            profile.Endpoint,
            profile.CredentialReference,
            null,
            SafeSearch: profile.SafeSearch,
            CountryCode: profile.CountryCode,
            SearchLanguage: profile.SearchLanguage), secretStore, clock);
        if (!created.IsSuccess)
        {
            return Unavailable(false);
        }

        using var provider = created.Value;
        return await provider.SearchAsync(request, cancellationToken);
    }

    private SearchProviderResponse Unavailable(bool retryable) => new(
        [],
        new SearchProviderFailure(Descriptor.Id, SearchFailureKind.Unavailable, retryable));
}
