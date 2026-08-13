using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentForge.Abstractions.Models;
using AgentForge.Abstractions.Time;
using AgentForge.Domain.Models;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Providers;

namespace AgentForge.Models;

internal interface ILocalModelProviderFactory
{
    DomainResult<IModelProvider> Create(ProviderProfile profile);
}

internal sealed class LocalModelProviderFactory(
    IModelContextPreparer contextPreparer,
    IClock clock) : ILocalModelProviderFactory
{
    public DomainResult<IModelProvider> Create(ProviderProfile profile)
    {
        if (profile is null || profile.Endpoint is null || profile.Capabilities is null ||
            profile.Id.Value == Guid.Empty || profile.InstallationId.Value == Guid.Empty ||
            profile.ProviderType is not ("vllm" or "openai-compatible") ||
            !profile.SecretReference.IsNoCredential ||
            !profile.Capabilities.TextGeneration || !profile.Capabilities.Streaming ||
            profile.Capabilities.Images || string.IsNullOrWhiteSpace(profile.Model))
        {
            return DomainResult.Fail<IModelProvider>(new DomainFailure(
                FailureCode.UnsupportedCapability,
                "Interactive MVP testing currently requires a credential-free local compatible text provider."));
        }

        var endpoint = ChatCompletionsEndpoint(profile.Endpoint);
        var location = EndpointDestinationPolicy.Infer(endpoint);
        if (location is not (ModelProviderDataLocation.Loopback or ModelProviderDataLocation.PrivateNetwork) ||
            endpoint.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(endpoint.UserInfo) ||
            !string.IsNullOrEmpty(endpoint.Query) || !string.IsNullOrEmpty(endpoint.Fragment))
        {
            return DomainResult.Fail<IModelProvider>(new DomainFailure(
                FailureCode.PolicyDenied,
                "Interactive MVP testing is restricted to an exact loopback or literal private-network provider."));
        }

        var observedAt = profile.UpdatedAt == default || profile.UpdatedAt > clock.UtcNow
            ? clock.UtcNow
            : profile.UpdatedAt;
        var capabilities = new List<ModelCapabilityEvidence>
        {
            Evidence(ModelCapability.TextGeneration, profile.Capabilities.EvidenceSource, observedAt),
            Evidence(ModelCapability.Streaming, profile.Capabilities.EvidenceSource, observedAt),
        };
        if (profile.Capabilities.ToolCalls)
        {
            capabilities.Add(Evidence(ModelCapability.ToolCalls, profile.Capabilities.EvidenceSource, observedAt));
        }

        var descriptor = new ModelProviderDescriptor(
            profile.Id,
            profile.ProviderType,
            profile.Model,
            capabilities,
            new ModelProviderRoutingEvidence(
                location,
                ModelCapabilityEvidenceSource.PolicyApproved,
                131_072,
                4_096,
                9_000,
                null,
                null,
                250,
                observedAt));
        var provider = OpenAiCompatibleModelProvider.Create(
            descriptor,
            new OpenAiCompatibleModelProviderOptions(
                endpoint,
                AllowInsecureHttp: endpoint.Scheme == "http",
                DisableThinking: true,
                DestinationDataLocation: location),
            contextPreparer,
            clock);
        return provider.IsSuccess
            ? DomainResult.Success<IModelProvider>(provider.Value)
            : DomainResult.Fail<IModelProvider>(provider.Failure!);
    }

    private static ModelCapabilityEvidence Evidence(
        ModelCapability capability,
        string source,
        DateTimeOffset observedAt) => new(
        capability,
        ModelCapabilityEvidenceSource.Probed,
        ModelCapabilityAvailability.Available,
        string.IsNullOrWhiteSpace(source) ? "Validated local provider profile." : source,
        observedAt);

    private static Uri ChatCompletionsEndpoint(Uri baseEndpoint)
    {
        if (baseEndpoint.AbsolutePath.EndsWith("/chat/completions", StringComparison.Ordinal))
        {
            return baseEndpoint;
        }

        var builder = new UriBuilder(baseEndpoint)
        {
            Path = baseEndpoint.AbsolutePath.TrimEnd('/') + "/chat/completions",
        };
        return builder.Uri;
    }
}

internal sealed class LocalModelInteractionService(
    ILocalModelProviderFactory providers) : ILocalModelInteractionService
{
    private static readonly JsonSerializerOptions EvidenceJson = new(JsonSerializerDefaults.Web);

    public async Task<DomainResult<LocalModelInteractionResult>> InvokeAsync(
        LocalModelInteractionRequest request,
        CancellationToken cancellationToken) =>
        await InvokeCoreAsync(request, null, cancellationToken);

    public async Task<DomainResult<LocalModelInteractionResult>> InvokeAsync(
        LocalModelInteractionRequest request,
        ILocalModelInteractionObserver observer,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(observer);
        return await InvokeCoreAsync(request, observer, cancellationToken);
    }

    private async Task<DomainResult<LocalModelInteractionResult>> InvokeCoreAsync(
        LocalModelInteractionRequest request,
        ILocalModelInteractionObserver? observer,
        CancellationToken cancellationToken)
    {
        if (request is null || request.RequestId.Value == Guid.Empty || request.Provider is null ||
            !Content(request.SystemInstruction, 8_192) || !Content(request.Prompt, 16_384) ||
            request.Limits is null || request.Limits.MaximumOutputTokens is < 1 or > 4_096 ||
            request.Limits.MaximumToolCalls != 0 || request.Limits.MaximumEvents is < 2 or > 8_192 ||
            request.Limits.MaximumWallClockSeconds is < 1 or > 120 ||
            !Text(request.CorrelationId.Value, 128))
        {
            return DomainResult.Fail<LocalModelInteractionResult>(new DomainFailure(
                FailureCode.ValidationFailure,
                "Local model interaction identity, prompt, or execution bounds are invalid."));
        }

        var created = providers.Create(request.Provider);
        if (!created.IsSuccess)
        {
            return DomainResult.Fail<LocalModelInteractionResult>(created.Failure!);
        }

        using var disposable = created.Value as IDisposable;
        var modelRequest = new ModelRequest(
            request.RequestId,
            request.Provider.Model,
            [
                new ModelMessage(ModelMessageRole.System, [new ModelTextContent(request.SystemInstruction)]),
                new ModelMessage(ModelMessageRole.User, [new ModelTextContent(request.Prompt)]),
            ],
            [],
            new ModelResponseFormat(ModelResponseFormatKind.Text),
            request.Limits with { },
            0.2m,
            1m,
            null,
            request.CorrelationId);
        var output = new StringBuilder();
        ModelUsage? usage = null;
        ModelCompletedEvent? completed = null;
        ModelStartedEvent? started = null;
        var eventCount = 0;
        await foreach (var item in created.Value.StreamAsync(modelRequest, cancellationToken))
        {
            eventCount++;
            switch (item)
            {
                case ModelStartedEvent value:
                    started = value;
                    if (observer is not null)
                    {
                        await observer.OnProgressAsync(new LocalModelInteractionProgress(
                            request.RequestId,
                            LocalModelInteractionProgressKind.Started,
                            ContextRedactionCount: value.ContextRedactionCount), cancellationToken);
                    }
                    break;
                case ModelTextDeltaEvent value:
                    if (output.Length + value.Delta.Length > 32_768)
                    {
                        return Failure(FailureCode.BudgetExceeded, "The local model response exceeded the interactive output bound.");
                    }
                    output.Append(value.Delta);
                    if (observer is not null)
                    {
                        await observer.OnProgressAsync(new LocalModelInteractionProgress(
                            request.RequestId,
                            LocalModelInteractionProgressKind.TextDelta,
                            TextDelta: value.Delta), cancellationToken);
                    }
                    break;
                case ModelUsageEvent value:
                    usage = value.Usage;
                    if (observer is not null)
                    {
                        await observer.OnProgressAsync(new LocalModelInteractionProgress(
                            request.RequestId,
                            LocalModelInteractionProgressKind.Usage,
                            Usage: value.Usage), cancellationToken);
                    }
                    break;
                case ModelCompletedEvent value:
                    completed = value;
                    break;
                case ModelErrorEvent value:
                    return DomainResult.Fail<LocalModelInteractionResult>(Map(value.Error));
                case ModelToolCallDeltaEvent or ModelToolCallCompletedEvent:
                    return Failure(FailureCode.PolicyDenied, "Interactive MVP testing does not permit model tool calls.");
                case ModelStructuredOutputEvent:
                    return Failure(FailureCode.UnsupportedCapability, "Interactive MVP testing accepts text responses only.");
            }
        }

        if (started is null || completed is null || eventCount > request.Limits.MaximumEvents ||
            string.IsNullOrWhiteSpace(output.ToString()))
        {
            return Failure(FailureCode.RecoverableExternalFailure,
                "The local provider stream ended without a complete non-empty text response.");
        }

        var text = output.ToString();
        var evidenceHash = Hash(new
        {
            requestId = request.RequestId.Value,
            providerId = request.Provider.Id.Value,
            request.Provider.Version,
            request.Provider.Model,
            outputHash = Hash(text),
            usage,
            completed.FinishReason,
            started.ContextRedactionCount,
            eventCount,
        });
        return DomainResult.Success(new LocalModelInteractionResult(
            request.RequestId,
            text,
            usage,
            completed.FinishReason,
            started.ContextRedactionCount,
            eventCount,
            evidenceHash));
    }

    private static DomainFailure Map(ModelProviderError error) => new(error.Code switch
    {
        ModelProviderErrorCode.UnsupportedCapability => FailureCode.UnsupportedCapability,
        ModelProviderErrorCode.BudgetExceeded => FailureCode.BudgetExceeded,
        ModelProviderErrorCode.PolicyDenied => FailureCode.PolicyDenied,
        ModelProviderErrorCode.ProviderUnavailable or ModelProviderErrorCode.RateLimited =>
            FailureCode.RecoverableExternalFailure,
        _ => FailureCode.ValidationFailure,
    }, error.Message, error.IsRetryable);

    private static DomainResult<LocalModelInteractionResult> Failure(FailureCode code, string message) =>
        DomainResult.Fail<LocalModelInteractionResult>(new DomainFailure(code, message));

    private static string Hash<T>(T value) =>
        $"sha256:{Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, EvidenceJson)))}";

    private static bool Text(string? value, int maximum) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximum && !value.Any(character => character == '\0');

    private static bool Content(string? value, int maximum) => Text(value, maximum) &&
        !value!.Any(character => char.IsControl(character) && character is not ('\r' or '\n' or '\t'));
}
