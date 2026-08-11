using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using System.Text;
using AgentForge.Abstractions.Models;
using AgentForge.Abstractions.Time;
using AgentForge.Domain.Models;
using AgentForge.Domain.Primitives;

namespace AgentForge.Models;

public sealed class DeterministicModelProvider : IModelProvider
{
    private const int MaximumSteps = 4096;
    private const int MaximumDeltaCharacters = 32_768;
    private const int MaximumJsonCharacters = 262_144;
    private readonly DeterministicModelScript _script;
    private readonly IClock _clock;
    private readonly string _capabilityEvidenceHash;

    private DeterministicModelProvider(
        ModelProviderDescriptor descriptor,
        DeterministicModelScript script,
        IClock clock)
    {
        Descriptor = descriptor;
        _script = script;
        _clock = clock;
        _capabilityEvidenceHash = ModelContractValidator.ComputeCapabilityEvidenceHash(descriptor);
    }

    public ModelProviderDescriptor Descriptor { get; }

    public static DomainResult<DeterministicModelProvider> Create(
        ModelProviderDescriptor descriptor,
        DeterministicModelScript script,
        IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        var normalizedDescriptor = ModelContractValidator.NormalizeDescriptor(descriptor);
        if (!normalizedDescriptor.IsSuccess)
        {
            return DomainResult.Fail<DeterministicModelProvider>(normalizedDescriptor.Failure!);
        }

        var normalizedScript = NormalizeScript(script, normalizedDescriptor.Value, clock.UtcNow);
        return normalizedScript.IsSuccess
            ? DomainResult.Success(new DeterministicModelProvider(
                normalizedDescriptor.Value,
                normalizedScript.Value,
                clock))
            : DomainResult.Fail<DeterministicModelProvider>(normalizedScript.Failure!);
    }

    public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
        ModelRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var prepared = ModelContractValidator.NormalizeRequest(request, Descriptor, _clock.UtcNow);
        if (!prepared.IsSuccess)
        {
            yield return ErrorEvent(request?.Id ?? default, 0, ToProviderError(prepared.Failure!));
            yield break;
        }

        var scriptValidation = ValidateScriptForRequest(_script, prepared.Value.Request);
        if (!scriptValidation.IsSuccess)
        {
            yield return ErrorEvent(prepared.Value.Request.Id, 0, ToProviderError(scriptValidation.Failure!));
            yield break;
        }

        using var duration = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        duration.CancelAfter(TimeSpan.FromSeconds(prepared.Value.Request.Limits.MaximumWallClockSeconds));
        var effectiveToken = duration.Token;
        long sequence = 0;
        yield return new ModelStartedEvent(
            prepared.Value.Request.Id,
            sequence++,
            _clock.UtcNow,
            Descriptor.ProfileId,
            Descriptor.ProviderType,
            Descriptor.Model,
            prepared.Value.InputHash,
            _capabilityEvidenceHash);

        foreach (var step in _script.Steps)
        {
            effectiveToken.ThrowIfCancellationRequested();
            switch (step)
            {
                case DeterministicDelayStep delay:
                    await Task.Delay(delay.Delay, effectiveToken);
                    break;
                case DeterministicTextStep text:
                    yield return new ModelTextDeltaEvent(
                        prepared.Value.Request.Id,
                        sequence++,
                        _clock.UtcNow,
                        text.Delta);
                    break;
                case DeterministicToolCallStep toolCall:
                    for (var index = 0; index < toolCall.ArgumentDeltas.Count; index++)
                    {
                        yield return new ModelToolCallDeltaEvent(
                            prepared.Value.Request.Id,
                            sequence++,
                            _clock.UtcNow,
                            toolCall.ToolCallId,
                            index == 0 ? toolCall.ToolName : null,
                            toolCall.ArgumentDeltas[index]);
                    }

                    yield return new ModelToolCallCompletedEvent(
                        prepared.Value.Request.Id,
                        sequence++,
                        _clock.UtcNow,
                        toolCall.ToolCallId,
                        toolCall.ToolName,
                        NormalizeJsonObject(string.Concat(toolCall.ArgumentDeltas)));
                    break;
                case DeterministicStructuredOutputStep structured:
                    yield return new ModelStructuredOutputEvent(
                        prepared.Value.Request.Id,
                        sequence++,
                        _clock.UtcNow,
                        structured.Json);
                    break;
                case DeterministicUsageStep usage:
                    yield return new ModelUsageEvent(
                        prepared.Value.Request.Id,
                        sequence++,
                        _clock.UtcNow,
                        usage.Usage);
                    break;
                case DeterministicFailureStep failure:
                    yield return ErrorEvent(
                        prepared.Value.Request.Id,
                        sequence,
                        failure.Error);
                    yield break;
                default:
                    throw new InvalidOperationException("Normalized deterministic model step was invalid.");
            }
        }

        effectiveToken.ThrowIfCancellationRequested();
        yield return new ModelCompletedEvent(
            prepared.Value.Request.Id,
            sequence,
            _clock.UtcNow,
            _script.FinishReason);
    }

    private ModelErrorEvent ErrorEvent(
        ModelRequestId requestId,
        long sequence,
        ModelProviderError error) => new(requestId, sequence, _clock.UtcNow, error);

    private static DomainResult<DeterministicModelScript> NormalizeScript(
        DeterministicModelScript script,
        ModelProviderDescriptor descriptor,
        DateTimeOffset evaluatedAt)
    {
        if (script is null || script.Steps is null || script.Steps.Count > MaximumSteps ||
            !Enum.IsDefined(script.FinishReason))
        {
            return Invalid<DeterministicModelScript>("Deterministic model script or finish reason is invalid.");
        }

        var normalized = new List<DeterministicModelStep>(script.Steps.Count);
        var toolCallIds = new HashSet<string>(StringComparer.Ordinal);
        var usageSteps = 0;
        for (var index = 0; index < script.Steps.Count; index++)
        {
            var step = script.Steps[index];
            switch (step)
            {
                case DeterministicDelayStep delay when delay.Delay >= TimeSpan.Zero &&
                    delay.Delay <= TimeSpan.FromMinutes(5):
                    normalized.Add(delay with { });
                    break;
                case DeterministicTextStep text when IsContent(text.Delta, MaximumDeltaCharacters):
                    normalized.Add(text with { });
                    break;
                case DeterministicToolCallStep toolCall when
                    ModelContractValidator.Supports(descriptor, ModelCapability.ToolCalls, evaluatedAt) &&
                    IsIdentifier(toolCall.ToolCallId, 256) && IsToolName(toolCall.ToolName) &&
                    toolCallIds.Add(toolCall.ToolCallId) &&
                    toolCall.ArgumentDeltas is not null && toolCall.ArgumentDeltas.Count is >= 1 and <= 1024 &&
                    toolCall.ArgumentDeltas.All(item => item is not null && item.Length <= MaximumDeltaCharacters) &&
                    ModelContractValidator.TryNormalizeJsonObject(
                        string.Concat(toolCall.ArgumentDeltas),
                        MaximumJsonCharacters,
                        out var arguments):
                    normalized.Add(toolCall with
                    {
                        ArgumentDeltas = Array.AsReadOnly(toolCall.ArgumentDeltas.ToArray()),
                    });
                    _ = arguments;
                    break;
                case DeterministicStructuredOutputStep structured when
                    ModelContractValidator.Supports(descriptor, ModelCapability.StructuredOutput, evaluatedAt) &&
                    ModelContractValidator.TryNormalizeJson(
                        structured.Json,
                        MaximumJsonCharacters,
                        out var json):
                    normalized.Add(structured with { Json = json! });
                    break;
                case DeterministicUsageStep usage when ValidateUsage(usage.Usage) && ++usageSteps == 1:
                    normalized.Add(new DeterministicUsageStep(usage.Usage with
                    {
                        Currency = usage.Usage.Currency?.ToUpperInvariant(),
                    }));
                    break;
                case DeterministicFailureStep failure when
                    index == script.Steps.Count - 1 && ValidateError(failure.Error):
                    normalized.Add(new DeterministicFailureStep(failure.Error with { }));
                    break;
                default:
                    return Invalid<DeterministicModelScript>(
                        "Deterministic model steps must be typed, bounded, capability-compatible, and terminally ordered.");
            }
        }

        return DomainResult.Success(script with
        {
            Steps = new ReadOnlyCollection<DeterministicModelStep>(normalized),
        });
    }

    private static DomainResult<bool> ValidateScriptForRequest(
        DeterministicModelScript script,
        ModelRequest request)
    {
        var toolCalls = script.Steps.OfType<DeterministicToolCallStep>().ToArray();
        var toolNames = request.Tools.Select(item => item.Name).ToHashSet(StringComparer.Ordinal);
        if (toolCalls.Any(call => !toolNames.Contains(call.ToolName)))
        {
            return Invalid<bool>("Scripted tool calls must match an exact request tool definition.");
        }

        if (toolCalls.Length > request.Limits.MaximumToolCalls)
        {
            return DomainResult.Fail<bool>(new DomainFailure(
                FailureCode.BudgetExceeded,
                "Scripted tool calls exceed the request tool-call budget."));
        }

        if (script.Steps.OfType<DeterministicStructuredOutputStep>().Any() &&
            request.ResponseFormat.Kind is ModelResponseFormatKind.Text)
        {
            return Invalid<bool>("Structured output requires a structured response format.");
        }

        var usage = script.Steps.OfType<DeterministicUsageStep>().SingleOrDefault()?.Usage;
        if (usage is not null && (usage.OutputTokens > request.Limits.MaximumOutputTokens ||
            usage.ToolCalls > request.Limits.MaximumToolCalls))
        {
            return DomainResult.Fail<bool>(new DomainFailure(
                FailureCode.BudgetExceeded,
                "Scripted usage exceeds the request budget."));
        }

        var eventCount = 2 + script.Steps.Sum(step => step switch
        {
            DeterministicDelayStep => 0,
            DeterministicToolCallStep call => call.ArgumentDeltas.Count + 1,
            _ => 1,
        });
        if (script.Steps.Count > 0 && script.Steps[script.Steps.Count - 1] is DeterministicFailureStep)
        {
            eventCount--;
        }

        return eventCount > request.Limits.MaximumEvents
            ? DomainResult.Fail<bool>(new DomainFailure(
                FailureCode.BudgetExceeded,
                "Scripted events exceed the request event budget."))
            : DomainResult.Success(true);
    }

    private static ModelProviderError ToProviderError(DomainFailure failure) => new(
        failure.Code switch
        {
            FailureCode.UnsupportedCapability => ModelProviderErrorCode.UnsupportedCapability,
            FailureCode.BudgetExceeded => ModelProviderErrorCode.BudgetExceeded,
            FailureCode.PolicyDenied => ModelProviderErrorCode.PolicyDenied,
            FailureCode.RecoverableExternalFailure => ModelProviderErrorCode.ProviderUnavailable,
            _ => ModelProviderErrorCode.InvalidRequest,
        },
        failure.Message,
        failure.IsRetryable);

    private static bool ValidateUsage(ModelUsage usage) => usage is not null &&
        usage.InputTokens >= 0 && usage.OutputTokens >= 0 && usage.ToolCalls >= 0 &&
        usage.Cost is null or >= 0 &&
        (usage.Cost is null && usage.Currency is null ||
            usage.Cost is not null && IsCurrency(usage.Currency));

    private static bool ValidateError(ModelProviderError error) => error is not null &&
        Enum.IsDefined(error.Code) && IsSingleLine(error.Message, 2048) &&
        error.StatusCode is null or >= 100 and <= 599 &&
        (error.RetryAfter is null || error.RetryAfter >= TimeSpan.Zero &&
            error.RetryAfter <= TimeSpan.FromDays(1));

    private static string NormalizeJsonObject(string value) =>
        ModelContractValidator.TryNormalizeJsonObject(
            value,
            MaximumJsonCharacters,
            out var normalized)
            ? normalized!
            : throw new InvalidOperationException("Normalized tool-call arguments became invalid.");

    private static bool IsCurrency(string? value) => value is { Length: 3 } && value.All(char.IsAsciiLetter);

    private static bool IsIdentifier(string? value, int maximumLength) =>
        IsSingleLine(value, maximumLength) && char.IsAsciiLetterOrDigit(value![0]) &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or ':' or '_' or '-');

    private static bool IsToolName(string? value) =>
        IsSingleLine(value, 128) && (char.IsAsciiLetter(value![0]) || value[0] == '_') &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');

    private static bool IsContent(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength &&
        value.All(character => !char.IsControl(character) || character is '\r' or '\n' or '\t');

    private static bool IsSingleLine(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength &&
        !value.Any(char.IsControl) && string.Equals(value, value.Trim(), StringComparison.Ordinal);

    private static DomainResult<T> Invalid<T>(string message) =>
        DomainResult.Fail<T>(new DomainFailure(FailureCode.ValidationFailure, message));
}
