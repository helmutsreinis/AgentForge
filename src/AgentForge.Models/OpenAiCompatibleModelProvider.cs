using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using AgentForge.Abstractions.Models;
using AgentForge.Abstractions.Security;
using AgentForge.Abstractions.Time;
using AgentForge.Domain.Models;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Providers;
using AgentForge.Domain.Security;

namespace AgentForge.Models;

public sealed class OpenAiCompatibleModelProvider : IModelProvider, IDisposable
{
    private readonly OpenAiCompatibleModelProviderOptions _options;
    private readonly HttpClient _httpClient;
    private readonly IClock _clock;
    private readonly IModelContextPreparer _contextPreparer;
    private readonly ISecretStore? _secretStore;
    private readonly SecretReference? _credentialReference;
    private readonly string _capabilityEvidenceHash;

    private OpenAiCompatibleModelProvider(
        ModelProviderDescriptor descriptor,
        OpenAiCompatibleModelProviderOptions options,
        HttpMessageHandler handler,
        IClock clock,
        IModelContextPreparer contextPreparer,
        ISecretStore? secretStore,
        SecretReference? credentialReference)
    {
        Descriptor = descriptor;
        _options = options;
        _httpClient = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        _clock = clock;
        _contextPreparer = contextPreparer;
        _secretStore = secretStore;
        _credentialReference = credentialReference;
        _capabilityEvidenceHash = ModelContractValidator.ComputeCapabilityEvidenceHash(descriptor);
    }

    public ModelProviderDescriptor Descriptor { get; }

    public static DomainResult<OpenAiCompatibleModelProvider> Create(
        ModelProviderDescriptor descriptor,
        OpenAiCompatibleModelProviderOptions options,
        IModelContextPreparer contextPreparer,
        IClock clock)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(contextPreparer);
        ArgumentNullException.ThrowIfNull(clock);
        if (!ValidateOptions(options))
        {
            return Invalid<OpenAiCompatibleModelProvider>("OpenAI-compatible endpoint or transport bounds are invalid.");
        }

        return CreateCore(
            descriptor,
            options,
            CreateSafeHandler(options),
            clock,
            contextPreparer,
            null,
            null);
    }

    public static DomainResult<OpenAiCompatibleModelProvider> CreateHosted(
        ProviderProfile profile,
        ModelProviderDescriptor descriptor,
        OpenAiCompatibleModelProviderOptions options,
        IModelContextPreparer contextPreparer,
        ISecretStore secretStore,
        IClock clock)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(contextPreparer);
        ArgumentNullException.ThrowIfNull(secretStore);
        ArgumentNullException.ThrowIfNull(clock);
        if (!ValidateHostedProfile(profile, descriptor, options, secretStore))
        {
            return Invalid<OpenAiCompatibleModelProvider>(
                "Hosted provider profile, endpoint, capabilities, or secret reference are not an exact safe match.");
        }

        return CreateCore(
            descriptor,
            options,
            CreateSafeHandler(options),
            clock,
            contextPreparer,
            secretStore,
            profile.SecretReference with { });
    }

    internal static DomainResult<OpenAiCompatibleModelProvider> CreateForTesting(
        ModelProviderDescriptor descriptor,
        OpenAiCompatibleModelProviderOptions options,
        HttpMessageHandler handler,
        IModelContextPreparer contextPreparer,
        IClock clock)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(contextPreparer);
        return CreateCore(descriptor, options, handler, clock, contextPreparer, null, null);
    }

    internal static DomainResult<OpenAiCompatibleModelProvider> CreateHostedForTesting(
        ProviderProfile profile,
        ModelProviderDescriptor descriptor,
        OpenAiCompatibleModelProviderOptions options,
        HttpMessageHandler handler,
        IModelContextPreparer contextPreparer,
        ISecretStore secretStore,
        IClock clock)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(contextPreparer);
        ArgumentNullException.ThrowIfNull(secretStore);
        if (!ValidateHostedProfile(profile, descriptor, options, secretStore))
        {
            handler.Dispose();
            return Invalid<OpenAiCompatibleModelProvider>(
                "Hosted provider profile, endpoint, capabilities, or secret reference are not an exact safe match.");
        }

        return CreateCore(
            descriptor,
            options,
            handler,
            clock,
            contextPreparer,
            secretStore,
            profile.SecretReference with { });
    }

    private static DomainResult<OpenAiCompatibleModelProvider> CreateCore(
        ModelProviderDescriptor descriptor,
        OpenAiCompatibleModelProviderOptions options,
        HttpMessageHandler handler,
        IClock clock,
        IModelContextPreparer contextPreparer,
        ISecretStore? secretStore,
        SecretReference? credentialReference)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(contextPreparer);
        var normalized = ModelContractValidator.NormalizeDescriptor(descriptor);
        if (!normalized.IsSuccess)
        {
            handler.Dispose();
            return DomainResult.Fail<OpenAiCompatibleModelProvider>(normalized.Failure!);
        }

        if (!string.Equals(normalized.Value.ProviderType, "openai-compatible", StringComparison.Ordinal) ||
            !ValidateOptions(options) ||
            (secretStore is null) != (credentialReference is null) ||
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
            clock,
            contextPreparer,
            secretStore,
            credentialReference));
    }

    public void Dispose() => _httpClient.Dispose();

    public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
        ModelRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var context = _contextPreparer.Prepare(request);
        if (!context.IsSuccess || context.Value is null || context.Value.Request is null ||
            context.Value.RedactionCount < 0 ||
            !string.Equals(context.Value.Policy, ModelContextPreparer.PolicyName, StringComparison.Ordinal))
        {
            yield return ErrorEvent(
                request?.Id ?? default,
                0,
                context.Failure is null
                    ? new ModelProviderError(
                        ModelProviderErrorCode.PolicyDenied,
                        "The model context did not pass the required preparation policy.",
                        false)
                    : ToProviderError(context.Failure));
            yield break;
        }

        var prepared = ModelContractValidator.NormalizeRequest(context.Value.Request, Descriptor, _clock.UtcNow);
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
            _capabilityEvidenceHash,
            context.Value.RedactionCount,
            context.Value.Policy);

        using var duration = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        duration.CancelAfter(TimeSpan.FromSeconds(prepared.Value.Request.Limits.MaximumWallClockSeconds));
        using var message = CreateRequestMessage(prepared.Value.Request, payload.Value);
        SecretLease? credentialLease = null;
        if (_credentialReference is not null)
        {
            DomainResult<SecretLease> materialized = default;
            ModelProviderError? credentialFailure = null;
            try
            {
                materialized = await _secretStore!.MaterializeAsync(_credentialReference, duration.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && duration.IsCancellationRequested)
            {
                credentialFailure = new ModelProviderError(
                    ModelProviderErrorCode.BudgetExceeded,
                    "Provider credential materialization exceeded the invocation wall-clock limit.",
                    false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                credentialFailure = new ModelProviderError(
                    ModelProviderErrorCode.ProviderUnavailable,
                    "Provider credential materialization was canceled by the configured store.",
                    true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                credentialFailure = new ModelProviderError(
                    ModelProviderErrorCode.ProviderUnavailable,
                    "Provider credential materialization failed in the configured store.",
                    true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (credentialFailure is not null)
            {
                yield return ErrorEvent(prepared.Value.Request.Id, nextSequence, credentialFailure);
                yield break;
            }

            if (!materialized.IsSuccess || materialized.Value is null)
            {
                yield return ErrorEvent(
                    prepared.Value.Request.Id,
                    nextSequence,
                    new ModelProviderError(
                        ModelProviderErrorCode.AuthenticationFailed,
                        "The provider credential was unavailable for this invocation.",
                        materialized.Failure?.IsRetryable ?? false));
                yield break;
            }

            credentialLease = materialized.Value;
            if (!TryApplyBearerCredential(message, credentialLease))
            {
                credentialLease.Dispose();
                credentialLease = null;
                yield return ErrorEvent(
                    prepared.Value.Request.Id,
                    nextSequence,
                    new ModelProviderError(
                        ModelProviderErrorCode.AuthenticationFailed,
                        "The provider credential cannot be represented by the bounded bearer transport.",
                        false));
                yield break;
            }
        }

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
        finally
        {
            message.Headers.Authorization = null;
            credentialLease?.Dispose();
            credentialLease = null;
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

    private static bool TryApplyBearerCredential(HttpRequestMessage message, SecretLease credential)
    {
        var value = credential.Value.Span;
        if (value.Length is < 1 or > 8192)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (character is < '!' or > '~')
            {
                return false;
            }
        }

        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", new string(value));
        return true;
    }

    private static bool ValidateHostedProfile(
        ProviderProfile profile,
        ModelProviderDescriptor descriptor,
        OpenAiCompatibleModelProviderOptions options,
        ISecretStore secretStore)
    {
        if (profile is null || descriptor is null || descriptor.Capabilities is null ||
            options.ChatCompletionsEndpoint is null ||
            profile.SecretReference is null ||
            profile.Capabilities is null || profile.Id.Value == Guid.Empty ||
            profile.InstallationId.Value == Guid.Empty || profile.Version < 1 ||
            profile.CreatedAt == default || profile.UpdatedAt < profile.CreatedAt ||
            string.IsNullOrWhiteSpace(profile.Name) || profile.Name.Length > 128 || profile.Name.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(profile.ActorId.Value) || profile.ActorId.Value.Length > 128 ||
            string.IsNullOrWhiteSpace(profile.CorrelationId.Value) || profile.CorrelationId.Value.Length > 128 ||
            profile.Endpoint is null || profile.Endpoint.Scheme != "https" ||
            !string.Equals(profile.ProviderType, "openai-compatible", StringComparison.Ordinal) ||
            profile.Id != descriptor.ProfileId ||
            !string.Equals(profile.ProviderType, descriptor.ProviderType, StringComparison.Ordinal) ||
            !string.Equals(profile.Model, descriptor.Model, StringComparison.Ordinal) ||
            Uri.Compare(
                profile.Endpoint,
                options.ChatCompletionsEndpoint,
                UriComponents.HttpRequestUrl,
                UriFormat.UriEscaped,
                StringComparison.Ordinal) != 0 ||
            !string.Equals(profile.SecretReference.Store, secretStore.StoreName, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(profile.SecretReference.Key) || profile.SecretReference.Key.Length > 512 ||
            profile.SecretReference.Key.Any(char.IsControl) ||
            !profile.Capabilities.TextGeneration || !profile.Capabilities.Streaming || profile.Capabilities.Images)
        {
            return false;
        }

        var destination = options.DestinationDataLocation ?? EndpointDestinationPolicy.Infer(options.ChatCompletionsEndpoint);
        return descriptor.Routing is { } routing && routing.DataLocation == destination &&
            routing.Source is ModelCapabilityEvidenceSource.PolicyApproved &&
            profile.Capabilities.ToolCalls == ModelContractValidator.Supports(descriptor, ModelCapability.ToolCalls);
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
            options.MaximumRequestBytes is >= 1024 and <= 16_777_216 &&
            options.DestinationDataLocation is not ModelProviderDataLocation.InProcess;
    }

    private static SocketsHttpHandler CreateSafeHandler(OpenAiCompatibleModelProviderOptions options) =>
        PolicyBoundSocketsHttpHandler.Create(
            options.ChatCompletionsEndpoint,
            options.DestinationDataLocation ?? EndpointDestinationPolicy.Infer(options.ChatCompletionsEndpoint));

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
