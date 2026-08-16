using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentForge.Abstractions.Models;
using AgentForge.Abstractions.Time;
using AgentForge.Domain.Models;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Providers;

namespace AgentForge.Models;

internal static class LocalModelInteractionBounds
{
    public const int MaximumOutputTokens = 262_144;
    public const int MaximumEvents = 300_000;
    public const int MaximumWallClockSeconds = 270;
    public const int MaximumOutputCharacters = 2_097_152;
}

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
                524_288,
                LocalModelInteractionBounds.MaximumOutputTokens,
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
        source.StartsWith("operator-policy-override-", StringComparison.Ordinal)
            ? ModelCapabilityEvidenceSource.Overridden
            : ModelCapabilityEvidenceSource.Probed,
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
            !Content(request.SystemInstruction, 24_576) || !Content(request.Prompt, 16_384) ||
            request.Limits is null || request.Limits.MaximumOutputTokens is < 1 or
                > LocalModelInteractionBounds.MaximumOutputTokens ||
            request.Limits.MaximumToolCalls is < 0 or > 32 || request.Limits.MaximumEvents is < 2 or
                > LocalModelInteractionBounds.MaximumEvents ||
            request.Limits.MaximumWallClockSeconds is < 1 or
                > LocalModelInteractionBounds.MaximumWallClockSeconds ||
            !Text(request.CorrelationId.Value, 128) || !ValidHistory(request.ConversationHistory) ||
            !ValidContinuation(request.ContinuationMessages) || !ValidTools(request.Tools, request.Limits.MaximumToolCalls))
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
        var messages = new List<ModelMessage>(
            (request.ConversationHistory?.Count ?? 0) + (request.ContinuationMessages?.Count ?? 0) + 2)
        {
            new(ModelMessageRole.System, [new ModelTextContent(request.SystemInstruction)]),
        };
        messages.AddRange(request.ConversationHistory ?? []);
        messages.Add(new ModelMessage(ModelMessageRole.User, [new ModelTextContent(request.Prompt)]));
        messages.AddRange(request.ContinuationMessages ?? []);
        var modelRequest = new ModelRequest(
            request.RequestId,
            request.Provider.Model,
            messages,
            request.Tools ?? [],
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
        var toolCalls = new List<LocalModelToolCall>();
        var eventCount = 0;
        var maximumOutputCharacters = (int)Math.Min(
            LocalModelInteractionBounds.MaximumOutputCharacters,
            Math.Max(32_768L, request.Limits.MaximumOutputTokens * 8L));
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
                    if ((long)output.Length + value.Delta.Length > maximumOutputCharacters)
                    {
                        return Failure(FailureCode.BudgetExceeded, "The local model response exceeded the interactive output bound.");
                    }
                    output.Append(value.Delta);
                    if (observer is not null && (request.Tools?.Count ?? 0) == 0)
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
                case ModelToolCallDeltaEvent:
                    break;
                case ModelToolCallCompletedEvent value:
                    if (request.Limits.MaximumToolCalls == 0)
                    {
                        return Failure(FailureCode.PolicyDenied,
                            "The model emitted a tool call without an exact request tool contract.");
                    }
                    if (toolCalls.Count >= request.Limits.MaximumToolCalls ||
                        !Text(value.ToolCallId, 256) || !Text(value.ToolName, 128) ||
                        !JsonObject(value.ArgumentsJson, 16_384))
                    {
                        return Failure(FailureCode.BudgetExceeded,
                            "The model exceeded the exact tool-call count or argument bounds.");
                    }
                    toolCalls.Add(new LocalModelToolCall(
                        value.ToolCallId, value.ToolName, value.ArgumentsJson));
                    break;
                case ModelStructuredOutputEvent:
                    return Failure(FailureCode.UnsupportedCapability, "Interactive MVP testing accepts text responses only.");
            }
        }

        if (started is null || completed is null || eventCount > request.Limits.MaximumEvents ||
            (toolCalls.Count == 0 && string.IsNullOrWhiteSpace(output.ToString())) ||
            (toolCalls.Count > 0 && completed.FinishReason is not ModelFinishReason.ToolCalls))
        {
            return Failure(FailureCode.RecoverableExternalFailure,
                "The local provider stream ended without a complete non-empty text response.");
        }

        var text = output.ToString();
        if (observer is not null && (request.Tools?.Count ?? 0) > 0 && toolCalls.Count == 0 && text.Length > 0)
        {
            await observer.OnProgressAsync(new LocalModelInteractionProgress(
                request.RequestId,
                LocalModelInteractionProgressKind.TextDelta,
                TextDelta: text), cancellationToken);
        }
        var evidenceHash = Hash(new
        {
            requestId = request.RequestId.Value,
            providerId = request.Provider.Id.Value,
            request.Provider.Version,
            request.Provider.Model,
            conversationHash = Hash(request.ConversationHistory ?? []),
            outputHash = Hash(text),
            toolCalls,
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
            evidenceHash)
        {
            ToolCalls = toolCalls.ToArray(),
        });
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

    private static bool JsonObject(string? value, int maximum)
    {
        if (!Content(value, maximum)) return false;
        try
        {
            using var document = JsonDocument.Parse(value!);
            return document.RootElement.ValueKind is JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool ValidTools(IReadOnlyList<ModelToolDefinition>? tools, int maximumToolCalls)
    {
        if (tools is null or { Count: 0 }) return maximumToolCalls == 0;
        return maximumToolCalls > 0 && tools.Count <= 8 &&
            tools.Select(item => item.Name).Distinct(StringComparer.Ordinal).Count() == tools.Count &&
            tools.All(item => item is not null && Text(item.Name, 128) && Content(item.Description, 1024) &&
                JsonObject(item.InputSchemaJson, 16_384));
    }

    private static bool ValidContinuation(IReadOnlyList<ModelMessage>? messages)
    {
        if (messages is null or { Count: 0 }) return true;
        if (messages.Count > 64) return false;
        long characters = 0;
        foreach (var message in messages)
        {
            if (message is null || message.Content is not { Count: 1 }) return false;
            switch (message.Role, message.Content[0])
            {
                case (ModelMessageRole.Assistant, ModelToolCallContent call)
                    when Text(call.ToolCallId, 256) && Text(call.ToolName, 128) &&
                        JsonObject(call.ArgumentsJson, 16_384):
                    characters += call.ArgumentsJson.Length;
                    break;
                case (ModelMessageRole.Tool, ModelToolResultContent result)
                    when Text(result.ToolCallId, 256) && Text(result.ToolName, 128) &&
                        JsonObject(result.ResultJson, 65_536):
                    characters += result.ResultJson.Length;
                    break;
                default:
                    return false;
            }
        }
        return characters <= 262_144;
    }

    private static bool ValidHistory(IReadOnlyList<ModelMessage>? history)
    {
        if (history is null) return true;
        if (history.Count > 40) return false;
        var totalCharacters = 0L;
        foreach (var message in history)
        {
            if (message is null || message.Role is not (ModelMessageRole.User or ModelMessageRole.Assistant) ||
                message.Content is not { Count: 1 } || message.Content[0] is not ModelTextContent text ||
                !Content(text.Text, 16_384))
            {
                return false;
            }
            totalCharacters += text.Text.Length;
        }
        return totalCharacters <= 131_072;
    }
}
