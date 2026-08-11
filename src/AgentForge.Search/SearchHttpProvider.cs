using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using AgentForge.Abstractions.Search;
using AgentForge.Abstractions.Security;
using AgentForge.Abstractions.Time;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Search;
using AgentForge.Domain.Security;

namespace AgentForge.Search;

public sealed record SearchHttpProviderOptions(
    Uri Endpoint,
    SecretReference CredentialReference,
    string? GoogleEngineId,
    int MaximumResponseBytes = 1_048_576,
    TimeSpan? Timeout = null);

public sealed class SearchHttpProvider : ISearchProvider, IDisposable
{
    private readonly SearchHttpProviderOptions _options;
    private readonly ISecretStore _secretStore;
    private readonly IClock _clock;
    private readonly HttpClient _client;

    private SearchHttpProvider(
        SearchProviderDescriptor descriptor,
        SearchHttpProviderOptions options,
        ISecretStore secretStore,
        IClock clock,
        HttpMessageHandler handler)
    {
        Descriptor = descriptor;
        _options = options;
        _secretStore = secretStore;
        _clock = clock;
        _client = new HttpClient(handler, true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public SearchProviderDescriptor Descriptor { get; }

    public static DomainResult<SearchHttpProvider> CreateBrave(
        string id,
        SearchHttpProviderOptions options,
        ISecretStore secretStore,
        IClock clock) => Create(id, SearchProviderKind.Brave, options, secretStore, clock, CreateHandler());

    public static DomainResult<SearchHttpProvider> CreateGoogle(
        string id,
        SearchHttpProviderOptions options,
        ISecretStore secretStore,
        IClock clock) => Create(id, SearchProviderKind.GoogleCustomSearch, options, secretStore, clock, CreateHandler());

    internal static DomainResult<SearchHttpProvider> CreateForTesting(
        string id,
        SearchProviderKind kind,
        SearchHttpProviderOptions options,
        ISecretStore secretStore,
        IClock clock,
        HttpMessageHandler handler) => Create(id, kind, options, secretStore, clock, handler);

    public async Task<SearchProviderResponse> SearchAsync(
        SearchRequest request,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.Timeout ?? TimeSpan.FromSeconds(15));
        var materialized = await _secretStore.MaterializeAsync(_options.CredentialReference, timeout.Token);
        if (!materialized.IsSuccess)
        {
            return Failure(SearchFailureKind.Unavailable, materialized.Failure?.IsRetryable == true);
        }

        await using var lease = materialized.Value;
        var credential = lease.Value.Span;
        if (credential.Length is < 1 or > 512 || credential.Contains('\r') || credential.Contains('\n'))
        {
            return Failure(SearchFailureKind.Unavailable, false);
        }

        using var message = new HttpRequestMessage(HttpMethod.Get, BuildUri(request));
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        var credentialValue = new string(credential);
        try
        {
            message.Headers.TryAddWithoutValidation(
                Descriptor.Kind == SearchProviderKind.Brave ? "X-Subscription-Token" : "X-Goog-Api-Key",
                credentialValue);
            using var response = await _client.SendAsync(
                message,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                return Failure(SearchFailureKind.Throttled, true);
            }

            if (response.StatusCode is HttpStatusCode.PaymentRequired or HttpStatusCode.Forbidden)
            {
                return Failure(SearchFailureKind.QuotaExceeded, false);
            }

            if (!response.IsSuccessStatusCode)
            {
                return Failure(SearchFailureKind.Unavailable, (int)response.StatusCode >= 500);
            }

            if (response.Content.Headers.ContentLength > _options.MaximumResponseBytes)
            {
                return Failure(SearchFailureKind.InvalidResponse, false);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var bounded = new MemoryStream(_options.MaximumResponseBytes);
            var buffer = new byte[8192];
            while (true)
            {
                var read = await stream.ReadAsync(buffer, timeout.Token);
                if (read == 0)
                {
                    break;
                }

                if (bounded.Length + read > _options.MaximumResponseBytes)
                {
                    return Failure(SearchFailureKind.InvalidResponse, false);
                }

                bounded.Write(buffer, 0, read);
            }

            return Parse(bounded.ToArray());
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure(SearchFailureKind.Unavailable, true);
        }
        catch (HttpRequestException)
        {
            return Failure(SearchFailureKind.Unavailable, true);
        }
        finally
        {
            message.Headers.Remove("X-Subscription-Token");
            message.Headers.Remove("X-Goog-Api-Key");
            // The unavoidable managed header value is invocation-local and never retained or persisted.
            credentialValue = string.Empty;
        }
    }

    public void Dispose() => _client.Dispose();

    private static DomainResult<SearchHttpProvider> Create(
        string id,
        SearchProviderKind kind,
        SearchHttpProviderOptions options,
        ISecretStore secretStore,
        IClock clock,
        HttpMessageHandler handler)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(secretStore);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(handler);
        var normalizedId = id.Trim().ToLowerInvariant();
        var expectedHost = kind == SearchProviderKind.Brave ? "api.search.brave.com" : "customsearch.googleapis.com";
        var expectedPath = kind == SearchProviderKind.Brave ? "/res/v1/web/search" : "/customsearch/v1";
        var effectiveTimeout = options.Timeout ?? TimeSpan.FromSeconds(15);
        var valid = normalizedId.Length is >= 1 and <= 128 &&
            normalizedId.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '.') &&
            kind is SearchProviderKind.Brave or SearchProviderKind.GoogleCustomSearch &&
            options.Endpoint.IsAbsoluteUri &&
            options.Endpoint.Scheme == Uri.UriSchemeHttps &&
            options.Endpoint.IsDefaultPort &&
            string.Equals(options.Endpoint.Host, expectedHost, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(options.Endpoint.AbsolutePath, expectedPath, StringComparison.Ordinal) &&
            string.IsNullOrEmpty(options.Endpoint.Query) &&
            string.IsNullOrEmpty(options.Endpoint.Fragment) &&
            options.MaximumResponseBytes is >= 1024 and <= 4_194_304 &&
            effectiveTimeout >= TimeSpan.FromSeconds(1) &&
            effectiveTimeout <= TimeSpan.FromSeconds(30) &&
            options.CredentialReference.Store == secretStore.StoreName &&
            options.CredentialReference.Key.Length is >= 1 and <= 512 &&
            (kind != SearchProviderKind.GoogleCustomSearch || IsEngineId(options.GoogleEngineId));
        if (!valid)
        {
            handler.Dispose();
            return DomainResult.Fail<SearchHttpProvider>(new DomainFailure(
                FailureCode.ValidationFailure,
                "Search provider identity, endpoint, credential reference, or bounds are invalid."));
        }

        var descriptor = new SearchProviderDescriptor(
            normalizedId,
            kind,
            true,
            kind == SearchProviderKind.Brave ? 10 : 20,
            SearchContractValidator.Hash($"v1:{normalizedId}:{kind}:{options.Endpoint}:{options.GoogleEngineId}"));
        return DomainResult.Success(new SearchHttpProvider(descriptor, options with { }, secretStore, clock, handler));
    }

    private Uri BuildUri(SearchRequest request)
    {
        var query = $"q={Uri.EscapeDataString(request.Query)}&count={request.MaximumResults}";
        if (Descriptor.Kind == SearchProviderKind.GoogleCustomSearch)
        {
            query = $"q={Uri.EscapeDataString(request.Query)}&num={Math.Min(request.MaximumResults, 10)}&cx={Uri.EscapeDataString(_options.GoogleEngineId!)}";
        }

        return new UriBuilder(_options.Endpoint) { Query = query }.Uri;
    }

    private SearchProviderResponse Parse(byte[] payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16,
            });
            var root = document.RootElement;
            var items = Descriptor.Kind == SearchProviderKind.Brave
                ? root.GetProperty("web").GetProperty("results")
                : root.GetProperty("items");
            var hits = ImmutableArray.CreateBuilder<SearchProviderHit>();
            var rank = 0;
            foreach (var item in items.EnumerateArray())
            {
                rank++;
                var urlName = Descriptor.Kind == SearchProviderKind.Brave ? "url" : "link";
                var snippetName = Descriptor.Kind == SearchProviderKind.Brave ? "description" : "snippet";
                if (!item.TryGetProperty(urlName, out var urlElement) ||
                    !item.TryGetProperty("title", out var titleElement) ||
                    !Uri.TryCreate(urlElement.GetString(), UriKind.Absolute, out var source) ||
                    source.Scheme is not ("https" or "http"))
                {
                    continue;
                }

                var snippet = item.TryGetProperty(snippetName, out var snippetElement) ? snippetElement.GetString() ?? string.Empty : string.Empty;
                hits.Add(new SearchProviderHit(
                    Descriptor.Id,
                    rank,
                    source,
                    Bound(titleElement.GetString() ?? string.Empty, 512),
                    Bound(snippet, 4096),
                    _clock.UtcNow));
            }

            return hits.Count == 0 ? Failure(SearchFailureKind.InvalidResponse, false) : new SearchProviderResponse(hits.ToImmutable(), null);
        }
        catch (JsonException)
        {
            return Failure(SearchFailureKind.InvalidResponse, false);
        }
        catch (InvalidOperationException)
        {
            return Failure(SearchFailureKind.InvalidResponse, false);
        }
        catch (KeyNotFoundException)
        {
            return Failure(SearchFailureKind.InvalidResponse, false);
        }
    }

    private SearchProviderResponse Failure(SearchFailureKind kind, bool retryable) =>
        new([], new SearchProviderFailure(Descriptor.Id, kind, retryable));

    private static string Bound(string value, int maximum) => value.Length <= maximum ? value : value[..maximum];

    private static bool IsEngineId(string? value) =>
        value is { Length: >= 1 and <= 128 } && !value.Any(char.IsControl);

    private static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    };
}
