using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using AgentForge.Abstractions.Models;
using AgentForge.Domain.Models;
using AgentForge.Domain.Primitives;

namespace AgentForge.Models;

internal sealed class OpenAiCompatibleModelDiscoveryService : IModelCatalogDiscoveryService
{
    private const int MaximumModels = 128;
    private const int MaximumResponseBytes = 1_048_576;
    private static readonly HashSet<string> SupportedProviderTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "openai",
        "deepseek",
        "vllm",
        "openai-compatible",
    };

    private readonly Func<Uri, ModelProviderDataLocation, HttpMessageHandler> _handlerFactory;

    public OpenAiCompatibleModelDiscoveryService()
        : this((endpoint, location) => PolicyBoundSocketsHttpHandler.Create(endpoint, location))
    {
    }

    internal OpenAiCompatibleModelDiscoveryService(
        Func<Uri, ModelProviderDataLocation, HttpMessageHandler> handlerFactory)
    {
        _handlerFactory = handlerFactory ?? throw new ArgumentNullException(nameof(handlerFactory));
    }

    public async Task<DomainResult<ModelCatalogDiscoveryResult>> DiscoverAsync(
        ModelCatalogDiscoveryRequest request,
        CancellationToken cancellationToken)
    {
        var validated = Validate(request?.BaseEndpoint, request?.ProviderType, request?.Credential ?? default);
        if (!validated.IsSuccess)
        {
            return DomainResult.Fail<ModelCatalogDiscoveryResult>(validated.Failure!);
        }

        var endpoint = Combine(request!.BaseEndpoint, "models");
        var response = await SendAsync(endpoint, HttpMethod.Get, null, request.Credential, cancellationToken);
        if (!response.IsSuccess)
        {
            return DomainResult.Fail<ModelCatalogDiscoveryResult>(response.Failure!);
        }

        try
        {
            using var document = JsonDocument.Parse(response.Value);
            if (!document.RootElement.TryGetProperty("data", out var data) ||
                data.ValueKind is not JsonValueKind.Array)
            {
                return Invalid<ModelCatalogDiscoveryResult>("The provider model catalog response is invalid.");
            }

            var models = new List<ModelCatalogEntry>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in data.EnumerateArray())
            {
                if (models.Count >= MaximumModels)
                {
                    return Invalid<ModelCatalogDiscoveryResult>("The provider returned too many models.");
                }

                if (!item.TryGetProperty("id", out var idElement) || idElement.ValueKind is not JsonValueKind.String)
                {
                    continue;
                }

                var id = idElement.GetString();
                if (!BoundedText(id, 256) || !ids.Add(id!))
                {
                    continue;
                }

                var ownedBy = item.TryGetProperty("owned_by", out var ownerElement) &&
                    ownerElement.ValueKind is JsonValueKind.String && BoundedText(ownerElement.GetString(), 128)
                        ? ownerElement.GetString()
                        : null;
                var maximumContext = item.TryGetProperty("max_model_len", out var contextElement) &&
                    contextElement.TryGetInt32(out var parsedContext) && parsedContext is > 0 and <= 16_777_216
                        ? (int?)parsedContext
                        : null;
                models.Add(new ModelCatalogEntry(id!, ownedBy, maximumContext));
            }

            if (models.Count == 0)
            {
                return Invalid<ModelCatalogDiscoveryResult>("The provider returned no usable model identifiers.");
            }

            return DomainResult.Success(new ModelCatalogDiscoveryResult(
                models.OrderBy(model => model.Id, StringComparer.OrdinalIgnoreCase).ToArray(),
                endpoint,
                DateTimeOffset.UtcNow));
        }
        catch (JsonException)
        {
            return Invalid<ModelCatalogDiscoveryResult>("The provider model catalog is not valid bounded JSON.");
        }
    }

    public async Task<DomainResult<ModelConnectionProbeResult>> ProbeAsync(
        ModelConnectionProbeRequest request,
        CancellationToken cancellationToken)
    {
        var validated = Validate(request?.BaseEndpoint, request?.ProviderType, request?.Credential ?? default);
        if (!validated.IsSuccess)
        {
            return DomainResult.Fail<ModelConnectionProbeResult>(validated.Failure!);
        }

        if (!BoundedText(request!.Model, 256))
        {
            return Invalid<ModelConnectionProbeResult>("A bounded model identifier is required.");
        }

        var endpoint = Combine(request.BaseEndpoint, "chat/completions");
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            model = request.Model,
            messages = new[] { new { role = "user", content = "Reply with OK." } },
            temperature = 0,
            max_tokens = 8,
            stream = false,
        });
        var stopwatch = Stopwatch.StartNew();
        var response = await SendAsync(endpoint, HttpMethod.Post, payload, request.Credential, cancellationToken);
        stopwatch.Stop();
        if (!response.IsSuccess)
        {
            return DomainResult.Fail<ModelConnectionProbeResult>(response.Failure!);
        }

        try
        {
            using var document = JsonDocument.Parse(response.Value);
            if (!document.RootElement.TryGetProperty("choices", out var choices) ||
                choices.ValueKind is not JsonValueKind.Array || choices.GetArrayLength() == 0)
            {
                return Invalid<ModelConnectionProbeResult>("The selected model returned no compatible response choice.");
            }
        }
        catch (JsonException)
        {
            return Invalid<ModelConnectionProbeResult>("The selected model returned invalid bounded JSON.");
        }

        return DomainResult.Success(new ModelConnectionProbeResult(
            request.Model,
            endpoint,
            stopwatch.Elapsed,
            "openai-compatible-chat-probe-v1"));
    }

    private async Task<DomainResult<byte[]>> SendAsync(
        Uri endpoint,
        HttpMethod method,
        byte[]? payload,
        ReadOnlyMemory<char> credential,
        CancellationToken cancellationToken)
    {
        var location = EndpointDestinationPolicy.Infer(endpoint);
        using var handler = _handlerFactory(endpoint, location);
        using var client = new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        using var message = new HttpRequestMessage(method, endpoint);
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        message.Headers.UserAgent.ParseAdd("AgentForge-Setup/1.0");
        if (!credential.IsEmpty)
        {
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", new string(credential.Span));
        }

        if (payload is not null)
        {
            message.Content = new ByteArrayContent(payload);
            message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        }

        using var duration = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        duration.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, duration.Token);
            message.Headers.Authorization = null;
            if (!response.IsSuccessStatusCode)
            {
                return DomainResult.Fail<byte[]>(new DomainFailure(
                    response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                        ? FailureCode.PolicyDenied
                        : FailureCode.RecoverableExternalFailure,
                    $"The provider returned HTTP status {(int)response.StatusCode}.",
                    IsRetryable: (int)response.StatusCode >= 500));
            }

            if (response.RequestMessage?.RequestUri is not { } actual ||
                Uri.Compare(actual, endpoint, UriComponents.HttpRequestUrl, UriFormat.UriEscaped, StringComparison.Ordinal) != 0 ||
                response.Content.Headers.ContentLength > MaximumResponseBytes)
            {
                return Invalid<byte[]>("The provider response crossed its approved endpoint or size boundary.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(duration.Token);
            using var buffer = new MemoryStream();
            var chunk = new byte[16_384];
            while (true)
            {
                var read = await stream.ReadAsync(chunk, duration.Token);
                if (read == 0)
                {
                    break;
                }

                if (buffer.Length + read > MaximumResponseBytes)
                {
                    return Invalid<byte[]>("The provider response exceeded its size boundary.");
                }

                buffer.Write(chunk, 0, read);
            }

            return DomainResult.Success(buffer.ToArray());
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return External<byte[]>("The provider connection timed out.");
        }
        catch (HttpRequestException)
        {
            return External<byte[]>("The provider endpoint could not be reached within its network policy.");
        }
        finally
        {
            message.Headers.Authorization = null;
            if (payload is not null)
            {
                Array.Clear(payload);
            }
        }
    }

    private static DomainResult<bool> Validate(
        Uri? endpoint,
        string? providerType,
        ReadOnlyMemory<char> credential)
    {
        if (endpoint is null || !endpoint.IsAbsoluteUri || endpoint.AbsoluteUri.Length > 2048 ||
            endpoint.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(endpoint.UserInfo) ||
            !string.IsNullOrEmpty(endpoint.Query) || !string.IsNullOrEmpty(endpoint.Fragment) ||
            !BoundedText(providerType, 64) || !SupportedProviderTypes.Contains(providerType!))
        {
            return Invalid<bool>("The provider endpoint or adapter type is invalid.");
        }

        var location = EndpointDestinationPolicy.Infer(endpoint);
        if (endpoint.Scheme == "http" && location is not (ModelProviderDataLocation.Loopback or ModelProviderDataLocation.PrivateNetwork))
        {
            return Invalid<bool>("Unencrypted provider discovery is allowed only on loopback or private networks.");
        }

        if (credential.Length > 8192 || credential.Span.ContainsAnyExceptInRange('!', '~'))
        {
            return Invalid<bool>("The provider credential cannot use the bounded bearer transport.");
        }

        return DomainResult.Success(true);
    }

    private static Uri Combine(Uri baseEndpoint, string relativePath)
    {
        var builder = new UriBuilder(baseEndpoint)
        {
            Path = baseEndpoint.AbsolutePath.TrimEnd('/') + "/",
        };
        return new Uri(builder.Uri, relativePath);
    }

    private static bool BoundedText(string? value, int maximum) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximum && !value.Any(char.IsControl);

    private static DomainResult<T> Invalid<T>(string message) =>
        DomainResult.Fail<T>(new DomainFailure(FailureCode.ValidationFailure, message));

    private static DomainResult<T> External<T>(string message) =>
        DomainResult.Fail<T>(new DomainFailure(FailureCode.RecoverableExternalFailure, message, IsRetryable: true));
}
