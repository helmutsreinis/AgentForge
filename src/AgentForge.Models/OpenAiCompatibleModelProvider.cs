using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using AgentForge.Abstractions.Models;
using AgentForge.Abstractions.Time;
using AgentForge.Domain.Models;
using AgentForge.Domain.Primitives;

namespace AgentForge.Models;

public sealed class OpenAiCompatibleModelProvider : IModelProvider, IDisposable
{
    private readonly OpenAiCompatibleModelProviderOptions _options;
    private readonly HttpClient _httpClient;
    private readonly IClock _clock;
    private readonly string _capabilityEvidenceHash;

    private OpenAiCompatibleModelProvider(
        ModelProviderDescriptor descriptor,
        OpenAiCompatibleModelProviderOptions options,
        HttpMessageHandler handler,
        IClock clock)
    {
        Descriptor = descriptor;
        _options = options;
        _httpClient = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        _clock = clock;
        _capabilityEvidenceHash = ModelContractValidator.ComputeCapabilityEvidenceHash(descriptor);
    }

    public ModelProviderDescriptor Descriptor { get; }

    public static DomainResult<OpenAiCompatibleModelProvider> Create(
        ModelProviderDescriptor descriptor,
        OpenAiCompatibleModelProviderOptions options,
        IClock clock)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);
        return CreateCore(descriptor, options, CreateSafeHandler(), clock);
    }

    internal static DomainResult<OpenAiCompatibleModelProvider> CreateForTesting(
        ModelProviderDescriptor descriptor,
        OpenAiCompatibleModelProviderOptions options,
        HttpMessageHandler handler,
        IClock clock)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return CreateCore(descriptor, options, handler, clock);
    }

    private static DomainResult<OpenAiCompatibleModelProvider> CreateCore(
        ModelProviderDescriptor descriptor,
        OpenAiCompatibleModelProviderOptions options,
        HttpMessageHandler handler,
        IClock clock)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(clock);
        var normalized = ModelContractValidator.NormalizeDescriptor(descriptor);
        if (!normalized.IsSuccess)
        {
            handler.Dispose();
            return DomainResult.Fail<OpenAiCompatibleModelProvider>(normalized.Failure!);
        }

        if (!string.Equals(normalized.Value.ProviderType, "openai-compatible", StringComparison.Ordinal) ||
            !ValidateOptions(options) ||
            ModelContractValidator.Supports(normalized.Value, ModelCapability.ImageInput) ||
            ModelContractValidator.Supports(normalized.Value, ModelCapability.AudioInput) ||
            ModelContractValidator.Supports(normalized.Value, ModelCapability.DocumentInput))
        {
            handler.Dispose();
            return Invalid<OpenAiCompatibleModelProvider>(
                "OpenAI-compatible adapter identity, endpoint, transport bounds, or capabilities are invalid.");
        }

        return DomainResult.Success(new OpenAiCompatibleModelProvider(
            normalized.Value,
            options with { },
            handler,
            clock));
    }

    public void Dispose() => _httpClient.Dispose();

    public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
        ModelRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var prepared = ModelContractValidator.NormalizeRequest(request, Descriptor, _clock.UtcNow);
        if (!prepared.IsSuccess)
        {
            yield return ErrorEvent(
                request?.Id ?? default,
                0,
                ToProviderError(prepared.Failure!));
            yield break;
        }

        var payload = OpenAiCompatibleRequestWriter.Write(prepared.Value.Request, _options);
        if (!payload.IsSuccess)
        {
            yield return ErrorEvent(
                prepared.Value.Request.Id,
                0,
                ToProviderError(payload.Failure!));
            yield break;
        }

        long nextSequence = 1;
        yield return new ModelStartedEvent(
            prepared.Value.Request.Id,
            0,
            _clock.UtcNow,
            Descriptor.ProfileId,
            Descriptor.ProviderType,
            Descriptor.Model,
            prepared.Value.InputHash,
            _capabilityEvidenceHash);

        using var duration = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        duration.CancelAfter(TimeSpan.FromSeconds(prepared.Value.Request.Limits.MaximumWallClockSeconds));
        using var message = CreateRequestMessage(prepared.Value.Request, payload.Value);
        HttpResponseMessage? response = null;
        ModelProviderError? sendFailure = null;
        try
        {
            response = await _httpClient.SendAsync(
                message,
                HttpCompletionOption.ResponseHeadersRead,
                duration.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && duration.IsCancellationRequested)
        {
            sendFailure = new ModelProviderError(
                ModelProviderErrorCode.BudgetExceeded,
                "The provider request exceeded its wall-clock limit.",
                false);
        }
        catch (HttpRequestException exception)
        {
            sendFailure = new ModelProviderError(
                ModelProviderErrorCode.ProviderUnavailable,
                "The provider transport was unavailable.",
                true,
                exception.StatusCode is null ? null : (int)exception.StatusCode.Value);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            sendFailure = new ModelProviderError(
                ModelProviderErrorCode.ProviderUnavailable,
                "The provider transport canceled the request.",
                true);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (sendFailure is not null)
        {
            yield return ErrorEvent(prepared.Value.Request.Id, nextSequence, sendFailure);
            yield break;
        }

        using (var successfulResponse = response!)
        {
            if (!successfulResponse.IsSuccessStatusCode)
            {
                yield return ErrorEvent(
                    prepared.Value.Request.Id,
                    nextSequence,
                    FromStatus(successfulResponse.StatusCode, successfulResponse.Headers.RetryAfter));
                yield break;
            }

            if (!IsExactResponseEndpoint(successfulResponse.RequestMessage?.RequestUri, _options.ChatCompletionsEndpoint))
            {
                yield return ErrorEvent(
                    prepared.Value.Request.Id,
                    nextSequence,
                    new ModelProviderError(
                        ModelProviderErrorCode.PolicyDenied,
                        "The provider response crossed an unapproved redirect boundary.",
                        false));
                yield break;
            }

            if (!string.Equals(
                    successfulResponse.Content.Headers.ContentType?.MediaType,
                    "text/event-stream",
                    StringComparison.OrdinalIgnoreCase) ||
                successfulResponse.Content.Headers.ContentLength > _options.MaximumResponseBytes)
            {
                yield return ErrorEvent(
                    prepared.Value.Request.Id,
                    nextSequence,
                    new ModelProviderError(
                        ModelProviderErrorCode.InvalidResponse,
                        "The provider did not return a bounded server-sent event stream.",
                        false));
                yield break;
            }

            Stream? responseStream = null;
            ModelProviderError? streamFailure = null;
            try
            {
                responseStream = await successfulResponse.Content.ReadAsStreamAsync(duration.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && duration.IsCancellationRequested)
            {
                streamFailure = new ModelProviderError(
                    ModelProviderErrorCode.BudgetExceeded,
                    "The provider stream exceeded its wall-clock limit.",
                    false);
            }
            catch (HttpRequestException)
            {
                streamFailure = new ModelProviderError(
                    ModelProviderErrorCode.ProviderUnavailable,
                    "The provider response stream was unavailable.",
                    true);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                streamFailure = new ModelProviderError(
                    ModelProviderErrorCode.ProviderUnavailable,
                    "The provider transport canceled the response stream.",
                    true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (streamFailure is not null)
            {
                yield return ErrorEvent(prepared.Value.Request.Id, nextSequence, streamFailure);
                yield break;
            }

            await using var reader = new BoundedServerSentEventReader(
                responseStream!,
                _options.MaximumEventBytes,
                _options.MaximumResponseBytes);
            var state = new OpenAiCompatibleStreamState(prepared.Value.Request, _clock);
            while (true)
            {
                SseReadResult read = default;
                ModelProviderError? readFailure = null;
                try
                {
                    read = await reader.ReadAsync(duration.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && duration.IsCancellationRequested)
                {
                    readFailure = new ModelProviderError(
                        ModelProviderErrorCode.BudgetExceeded,
                        "The provider stream exceeded its wall-clock limit.",
                        false);
                }
                catch (IOException)
                {
                    readFailure = new ModelProviderError(
                        ModelProviderErrorCode.ProviderUnavailable,
                        "The provider stream ended with a transport failure.",
                        true);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    readFailure = new ModelProviderError(
                        ModelProviderErrorCode.ProviderUnavailable,
                        "The provider transport canceled the response stream.",
                        true);
                }
                catch (ObjectDisposedException)
                {
                    readFailure = new ModelProviderError(
                        ModelProviderErrorCode.ProviderUnavailable,
                        "The provider response stream closed unexpectedly.",
                        true);
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (readFailure is not null)
                {
                    yield return ErrorEvent(prepared.Value.Request.Id, nextSequence, readFailure);
                    yield break;
                }

                StreamTranslation translation;
                if (read.Error is not null)
                {
                    translation = new StreamTranslation(
                        [ErrorEvent(
                            prepared.Value.Request.Id,
                            nextSequence,
                            new ModelProviderError(
                                ModelProviderErrorCode.InvalidResponse,
                                read.Error,
                                false))],
                        true);
                }
                else if (read.IsEndOfStream)
                {
                    translation = state.EndOfStream();
                }
                else
                {
                    translation = state.Process(read.Data!);
                }

                foreach (var item in translation.Events)
                {
                    nextSequence = item.Sequence + 1;
                    yield return item;
                }

                if (translation.IsTerminal)
                {
                    yield break;
                }
            }
        }
    }

    private HttpRequestMessage CreateRequestMessage(ModelRequest request, byte[] payload)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, _options.ChatCompletionsEndpoint)
        {
            Content = new ByteArrayContent(payload),
        };
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        message.Headers.TryAddWithoutValidation("X-Correlation-Id", request.CorrelationId.Value);
        message.Headers.UserAgent.ParseAdd("AgentForge/0.3");
        message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json")
        {
            CharSet = "utf-8",
        };
        return message;
    }

    private ModelErrorEvent ErrorEvent(
        ModelRequestId requestId,
        long sequence,
        ModelProviderError error) => new(requestId, sequence, _clock.UtcNow, error);

    private ModelProviderError FromStatus(HttpStatusCode statusCode, RetryConditionHeaderValue? retryAfter)
    {
        var code = (int)statusCode;
        var retryable = statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests or
            HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or
            HttpStatusCode.GatewayTimeout || code == 425;
        var mapped = statusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => ModelProviderErrorCode.AuthenticationFailed,
            HttpStatusCode.TooManyRequests => ModelProviderErrorCode.RateLimited,
            HttpStatusCode.RequestEntityTooLarge => ModelProviderErrorCode.BudgetExceeded,
            (HttpStatusCode)451 => ModelProviderErrorCode.PolicyDenied,
            >= HttpStatusCode.InternalServerError => ModelProviderErrorCode.ProviderUnavailable,
            _ when retryable => ModelProviderErrorCode.ProviderUnavailable,
            _ => ModelProviderErrorCode.InvalidRequest,
        };
        return new ModelProviderError(
            mapped,
            $"The provider returned HTTP status {code}.",
            retryable,
            code,
            GetRetryAfter(retryAfter));
    }

    private TimeSpan? GetRetryAfter(RetryConditionHeaderValue? value)
    {
        if (value?.Delta is { } delta && delta >= TimeSpan.Zero && delta <= TimeSpan.FromDays(1))
        {
            return delta;
        }

        if (value?.Date is { } date)
        {
            var delay = date - _clock.UtcNow;
            return delay >= TimeSpan.Zero && delay <= TimeSpan.FromDays(1) ? delay : null;
        }

        return null;
    }

    private static ModelProviderError ToProviderError(DomainFailure failure) => new(
        failure.Code switch
        {
            FailureCode.UnsupportedCapability => ModelProviderErrorCode.UnsupportedCapability,
            FailureCode.PolicyDenied or FailureCode.ApprovalRequired => ModelProviderErrorCode.PolicyDenied,
            FailureCode.BudgetExceeded => ModelProviderErrorCode.BudgetExceeded,
            FailureCode.RecoverableExternalFailure => ModelProviderErrorCode.ProviderUnavailable,
            _ => ModelProviderErrorCode.InvalidRequest,
        },
        failure.Message,
        failure.IsRetryable);

    private static bool ValidateOptions(OpenAiCompatibleModelProviderOptions options)
    {
        var endpoint = options.ChatCompletionsEndpoint;
        return endpoint is not null && endpoint.IsAbsoluteUri && endpoint.AbsoluteUri.Length <= 2048 &&
            endpoint.Scheme is "http" or "https" &&
            (endpoint.Scheme == "https" || options.AllowInsecureHttp) &&
            string.IsNullOrEmpty(endpoint.UserInfo) && string.IsNullOrEmpty(endpoint.Query) &&
            string.IsNullOrEmpty(endpoint.Fragment) && endpoint.HostNameType is not UriHostNameType.Unknown &&
            options.MaximumEventBytes is >= 1024 and <= 4_194_304 &&
            options.MaximumResponseBytes >= options.MaximumEventBytes &&
            options.MaximumResponseBytes <= 268_435_456 &&
            options.MaximumRequestBytes is >= 1024 and <= 16_777_216;
    }

    private static SocketsHttpHandler CreateSafeHandler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false,
        AutomaticDecompression = DecompressionMethods.None,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        MaxConnectionsPerServer = 4,
        MaxResponseHeadersLength = 16,
    };

    private static bool IsExactResponseEndpoint(Uri? actual, Uri expected) =>
        actual is not null && Uri.Compare(
            actual,
            expected,
            UriComponents.HttpRequestUrl,
            UriFormat.UriEscaped,
            StringComparison.Ordinal) == 0;

    private static DomainResult<T> Invalid<T>(string message) =>
        DomainResult.Fail<T>(new DomainFailure(FailureCode.ValidationFailure, message));
}
