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

public sealed class AnthropicModelProvider : IModelProvider, IDisposable
{
    private readonly AnthropicModelProviderOptions _options;
    private readonly HttpClient _httpClient;
    private readonly IClock _clock;
    private readonly IModelContextPreparer _contextPreparer;
    private readonly ISecretStore? _secretStore;
    private readonly SecretReference? _credentialReference;
    private readonly string _capabilityEvidenceHash;

    private AnthropicModelProvider(
        ModelProviderDescriptor descriptor,
        AnthropicModelProviderOptions options,
        HttpMessageHandler handler,
        IModelContextPreparer contextPreparer,
        ISecretStore? secretStore,
        SecretReference? credentialReference,
        IClock clock)
    {
        Descriptor = descriptor;
        _options = options;
        _httpClient = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        _contextPreparer = contextPreparer;
        _secretStore = secretStore;
        _credentialReference = credentialReference;
        _clock = clock;
        _capabilityEvidenceHash = ModelContractValidator.ComputeCapabilityEvidenceHash(descriptor);
    }

    public ModelProviderDescriptor Descriptor { get; }

    public static DomainResult<AnthropicModelProvider> Create(
        ModelProviderDescriptor descriptor,
        AnthropicModelProviderOptions options,
        IModelContextPreparer contextPreparer,
        IClock clock)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(contextPreparer);
        ArgumentNullException.ThrowIfNull(clock);
        if (!ValidateOptions(options))
        {
            return Invalid("Anthropic endpoint or transport bounds are invalid.");
        }

        return CreateCore(
            descriptor,
            options,
            PolicyBoundSocketsHttpHandler.Create(options.MessagesEndpoint, options.DestinationDataLocation),
            contextPreparer,
            null,
            null,
            clock);
    }

    public static DomainResult<AnthropicModelProvider> CreateHosted(
        ProviderProfile profile,
        ModelProviderDescriptor descriptor,
        AnthropicModelProviderOptions options,
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
            return Invalid("Anthropic profile, routing, endpoint, capabilities, or secret reference did not match.");
        }

        return CreateCore(
            descriptor,
            options,
            PolicyBoundSocketsHttpHandler.Create(options.MessagesEndpoint, options.DestinationDataLocation),
            contextPreparer,
            secretStore,
            profile.SecretReference with { },
            clock);
    }

    internal static DomainResult<AnthropicModelProvider> CreateForTesting(
        ModelProviderDescriptor descriptor,
        AnthropicModelProviderOptions options,
        HttpMessageHandler handler,
        IModelContextPreparer contextPreparer,
        IClock clock) =>
        CreateCore(descriptor, options, handler, contextPreparer, null, null, clock);

    internal static DomainResult<AnthropicModelProvider> CreateHostedForTesting(
        ProviderProfile profile,
        ModelProviderDescriptor descriptor,
        AnthropicModelProviderOptions options,
        HttpMessageHandler handler,
        IModelContextPreparer contextPreparer,
        ISecretStore secretStore,
        IClock clock)
    {
        if (!ValidateHostedProfile(profile, descriptor, options, secretStore))
        {
            handler.Dispose();
            return Invalid("Anthropic profile, routing, endpoint, capabilities, or secret reference did not match.");
        }

        return CreateCore(
            descriptor,
            options,
            handler,
            contextPreparer,
            secretStore,
            profile.SecretReference with { },
            clock);
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
            yield return Error(
                request?.Id ?? default,
                0,
                context.Failure is null
                    ? new ModelProviderError(ModelProviderErrorCode.PolicyDenied, "Model context preparation failed.", false)
                    : ToProviderError(context.Failure));
            yield break;
        }

        var prepared = ModelContractValidator.NormalizeRequest(context.Value.Request, Descriptor, _clock.UtcNow);
        if (!prepared.IsSuccess)
        {
            yield return Error(request?.Id ?? default, 0, ToProviderError(prepared.Failure!));
            yield break;
        }

        var payload = AnthropicRequestWriter.Write(prepared.Value.Request, _options);
        if (!payload.IsSuccess)
        {
            yield return Error(prepared.Value.Request.Id, 0, ToProviderError(payload.Failure!));
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
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                credentialFailure = new ModelProviderError(
                    ModelProviderErrorCode.ProviderUnavailable,
                    "Anthropic credential materialization was canceled.",
                    true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                credentialFailure = new ModelProviderError(
                    ModelProviderErrorCode.ProviderUnavailable,
                    "Anthropic credential materialization failed.",
                    true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (credentialFailure is not null)
            {
                yield return Error(prepared.Value.Request.Id, nextSequence, credentialFailure);
                yield break;
            }

            if (!materialized.IsSuccess || materialized.Value is null)
            {
                yield return Error(
                    prepared.Value.Request.Id,
                    nextSequence,
                    new ModelProviderError(ModelProviderErrorCode.AuthenticationFailed, "Anthropic credential was unavailable.", materialized.Failure?.IsRetryable ?? false));
                yield break;
            }

            credentialLease = materialized.Value;
            if (!TryApplyApiKey(message, credentialLease))
            {
                credentialLease.Dispose();
                credentialLease = null;
                yield return Error(
                    prepared.Value.Request.Id,
                    nextSequence,
                    new ModelProviderError(ModelProviderErrorCode.AuthenticationFailed, "Anthropic credential was not header-safe.", false));
                yield break;
            }
        }

        HttpResponseMessage? response = null;
        ModelProviderError? sendFailure = null;
        try
        {
            response = await _httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, duration.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && duration.IsCancellationRequested)
        {
            sendFailure = new ModelProviderError(ModelProviderErrorCode.BudgetExceeded, "Anthropic request exceeded its wall-clock limit.", false);
        }
        catch (HttpRequestException exception)
        {
            sendFailure = new ModelProviderError(
                ModelProviderErrorCode.ProviderUnavailable,
                "Anthropic transport was unavailable.",
                true,
                exception.StatusCode is null ? null : (int)exception.StatusCode.Value);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            sendFailure = new ModelProviderError(ModelProviderErrorCode.ProviderUnavailable, "Anthropic transport canceled the request.", true);
        }
        finally
        {
            message.Headers.Remove("x-api-key");
            credentialLease?.Dispose();
            credentialLease = null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (sendFailure is not null)
        {
            yield return Error(prepared.Value.Request.Id, nextSequence, sendFailure);
            yield break;
        }

        using (var successfulResponse = response!)
        {
            if (!successfulResponse.IsSuccessStatusCode)
            {
                yield return Error(
                    prepared.Value.Request.Id,
                    nextSequence,
                    FromStatus(successfulResponse.StatusCode, successfulResponse.Headers.RetryAfter));
                yield break;
            }

            if (!IsExactEndpoint(successfulResponse.RequestMessage?.RequestUri, _options.MessagesEndpoint) ||
                !string.Equals(successfulResponse.Content.Headers.ContentType?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase) ||
                successfulResponse.Content.Headers.ContentLength > _options.MaximumResponseBytes)
            {
                yield return Error(
                    prepared.Value.Request.Id,
                    nextSequence,
                    new ModelProviderError(ModelProviderErrorCode.InvalidResponse, "Anthropic response crossed its endpoint or stream boundary.", false));
                yield break;
            }

            Stream? responseStream = null;
            ModelProviderError? streamFailure = null;
            try
            {
                responseStream = await successfulResponse.Content.ReadAsStreamAsync(duration.Token);
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException or OperationCanceledException)
            {
                streamFailure = new ModelProviderError(
                    ModelProviderErrorCode.ProviderUnavailable,
                    "Anthropic response stream was unavailable.",
                    true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (streamFailure is not null)
            {
                yield return Error(prepared.Value.Request.Id, nextSequence, streamFailure);
                yield break;
            }

            await using var reader = new BoundedServerSentEventReader(
                responseStream!,
                _options.MaximumEventBytes,
                _options.MaximumResponseBytes);
            var state = new AnthropicStreamState(prepared.Value.Request, _clock);
            while (true)
            {
                SseReadResult read = default;
                ModelProviderError? readFailure = null;
                try
                {
                    read = await reader.ReadAsync(duration.Token);
                }
                catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException)
                {
                    readFailure = new ModelProviderError(
                        ModelProviderErrorCode.ProviderUnavailable,
                        "Anthropic stream ended with a transport failure.",
                        true);
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (readFailure is not null)
                {
                    yield return Error(prepared.Value.Request.Id, nextSequence, readFailure);
                    yield break;
                }

                StreamTranslation translation;
                if (read.Error is not null)
                {
                    translation = new StreamTranslation(
                        [Error(
                            prepared.Value.Request.Id,
                            nextSequence,
                            new ModelProviderError(ModelProviderErrorCode.InvalidResponse, read.Error, false))],
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

    private static DomainResult<AnthropicModelProvider> CreateCore(
        ModelProviderDescriptor descriptor,
        AnthropicModelProviderOptions options,
        HttpMessageHandler handler,
        IModelContextPreparer contextPreparer,
        ISecretStore? secretStore,
        SecretReference? credentialReference,
        IClock clock)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var normalized = ModelContractValidator.NormalizeDescriptor(descriptor);
        if (!normalized.IsSuccess)
        {
            handler.Dispose();
            return DomainResult.Fail<AnthropicModelProvider>(normalized.Failure!);
        }

        if (!string.Equals(normalized.Value.ProviderType, "anthropic", StringComparison.Ordinal) ||
            !ValidateOptions(options) || (secretStore is null) != (credentialReference is null) ||
            ModelContractValidator.Supports(normalized.Value, ModelCapability.ImageInput) ||
            ModelContractValidator.Supports(normalized.Value, ModelCapability.AudioInput) ||
            ModelContractValidator.Supports(normalized.Value, ModelCapability.DocumentInput) ||
            ModelContractValidator.Supports(normalized.Value, ModelCapability.StructuredOutput))
        {
            handler.Dispose();
            return Invalid("Anthropic adapter identity, capabilities, endpoint, or bounds are invalid.");
        }

        return DomainResult.Success(new AnthropicModelProvider(
            normalized.Value,
            options with { },
            handler,
            contextPreparer,
            secretStore,
            credentialReference,
            clock));
    }

    private HttpRequestMessage CreateRequestMessage(ModelRequest request, byte[] payload)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, _options.MessagesEndpoint)
        {
            Content = new ByteArrayContent(payload),
        };
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        message.Headers.TryAddWithoutValidation("anthropic-version", _options.ApiVersion);
        message.Headers.TryAddWithoutValidation("X-Correlation-Id", request.CorrelationId.Value);
        message.Headers.UserAgent.ParseAdd("AgentForge/0.3");
        message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        return message;
    }

    private static bool TryApplyApiKey(HttpRequestMessage message, SecretLease credential)
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

        return message.Headers.TryAddWithoutValidation("x-api-key", new string(value));
    }

    private static bool ValidateHostedProfile(
        ProviderProfile profile,
        ModelProviderDescriptor descriptor,
        AnthropicModelProviderOptions options,
        ISecretStore secretStore) =>
        profile is not null && descriptor is not null && descriptor.Routing is { } routing &&
        ValidateOptions(options) && profile.Id.Value != Guid.Empty && profile.InstallationId.Value != Guid.Empty &&
        profile.Version >= 1 && profile.Endpoint.Scheme == "https" &&
        string.Equals(profile.ProviderType, "anthropic", StringComparison.Ordinal) &&
        profile.Id == descriptor.ProfileId && string.Equals(profile.ProviderType, descriptor.ProviderType, StringComparison.Ordinal) &&
        string.Equals(profile.Model, descriptor.Model, StringComparison.Ordinal) &&
        IsExactEndpoint(profile.Endpoint, options.MessagesEndpoint) &&
        string.Equals(profile.SecretReference.Store, secretStore.StoreName, StringComparison.Ordinal) &&
        !string.IsNullOrWhiteSpace(profile.SecretReference.Key) && profile.SecretReference.Key.Length <= 512 &&
        profile.Capabilities.TextGeneration && profile.Capabilities.Streaming && !profile.Capabilities.Images &&
        profile.Capabilities.ToolCalls == ModelContractValidator.Supports(descriptor, ModelCapability.ToolCalls) &&
        routing.DataLocation == options.DestinationDataLocation &&
        routing.Source is ModelCapabilityEvidenceSource.PolicyApproved;

    private static bool ValidateOptions(AnthropicModelProviderOptions options) =>
        options is not null && options.MessagesEndpoint is { IsAbsoluteUri: true } endpoint &&
        endpoint.Scheme == "https" && endpoint.AbsoluteUri.Length <= 2048 &&
        string.IsNullOrEmpty(endpoint.UserInfo) && string.IsNullOrEmpty(endpoint.Query) &&
        string.IsNullOrEmpty(endpoint.Fragment) && options.DestinationDataLocation is ModelProviderDataLocation.Cloud &&
        options.ApiVersion.Length is >= 1 and <= 32 &&
        options.ApiVersion.All(character => char.IsAsciiDigit(character) || character == '-') &&
        options.MaximumEventBytes is >= 1024 and <= 4_194_304 &&
        options.MaximumResponseBytes >= options.MaximumEventBytes && options.MaximumResponseBytes <= 268_435_456 &&
        options.MaximumRequestBytes is >= 1024 and <= 16_777_216;

    private ModelErrorEvent Error(ModelRequestId requestId, long sequence, ModelProviderError error) =>
        new(requestId, sequence, _clock.UtcNow, error);

    private static ModelProviderError FromStatus(HttpStatusCode status, RetryConditionHeaderValue? retryAfter)
    {
        var code = (int)status;
        var mapped = status switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => ModelProviderErrorCode.AuthenticationFailed,
            HttpStatusCode.TooManyRequests => ModelProviderErrorCode.RateLimited,
            HttpStatusCode.RequestEntityTooLarge => ModelProviderErrorCode.BudgetExceeded,
            >= HttpStatusCode.InternalServerError => ModelProviderErrorCode.ProviderUnavailable,
            _ => ModelProviderErrorCode.InvalidRequest,
        };
        var retryable = mapped is ModelProviderErrorCode.RateLimited or ModelProviderErrorCode.ProviderUnavailable;
        TimeSpan? delay = null;
        if (retryAfter?.Delta is { } delta && delta >= TimeSpan.Zero && delta <= TimeSpan.FromDays(1))
        {
            delay = delta;
        }

        return new ModelProviderError(mapped, $"Anthropic returned HTTP status {code}.", retryable, code, delay);
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

    private static bool IsExactEndpoint(Uri? actual, Uri expected) => actual is not null && Uri.Compare(
        actual,
        expected,
        UriComponents.HttpRequestUrl,
        UriFormat.UriEscaped,
        StringComparison.Ordinal) == 0;

    private static DomainResult<AnthropicModelProvider> Invalid(string message) =>
        DomainResult.Fail<AnthropicModelProvider>(new DomainFailure(FailureCode.ValidationFailure, message));
}
