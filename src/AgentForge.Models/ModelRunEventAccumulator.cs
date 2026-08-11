using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentForge.Domain.Models;
using AgentForge.Domain.Primitives;

namespace AgentForge.Models;

internal sealed class ModelRunEventAccumulator(
    ModelRunRecord run,
    ModelRunBudgetReservation? attemptReservation = null,
    DateTimeOffset? attemptStartedAt = null) : IDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private bool _disposed;
    private bool _terminal;
    private bool _usageObserved;
    private DateTimeOffset? _lastTimestamp;

    public int EventCount { get; private set; }

    public ModelUsage Usage { get; private set; } = new(0, 0, 0, null, null);

    public ModelFinishReason? FinishReason { get; private set; }

    public ModelProviderError? ProviderError { get; private set; }

    public DomainResult<bool> Accept(ModelStreamEvent modelEvent)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (modelEvent is null || modelEvent.RequestId != run.RequestId ||
            modelEvent.Sequence != EventCount || modelEvent.Timestamp == default ||
            _lastTimestamp is { } lastTimestamp && modelEvent.Timestamp < lastTimestamp ||
            (attemptStartedAt ?? run.StartedAt) is { } startedAt && modelEvent.Timestamp < startedAt ||
            run.Lease is { } lease && modelEvent.Timestamp > lease.ExpiresAt || _terminal)
        {
            return Invalid("Model provider stream identity, sequence, or terminal ordering is invalid.");
        }

        var structural = ValidateEvent(modelEvent);
        if (!structural.IsSuccess)
        {
            return structural;
        }

        Append(modelEvent);
        EventCount++;
        _lastTimestamp = modelEvent.Timestamp;
        if (EventCount > (attemptReservation ?? run.Reservation).Events)
        {
            return DomainResult.Fail<bool>(new DomainFailure(
                FailureCode.BudgetExceeded,
                "Model provider stream exceeded the reserved event budget."));
        }

        switch (modelEvent)
        {
            case ModelUsageEvent usage:
                Usage = usage.Usage with { Currency = usage.Usage.Currency?.ToUpperInvariant() };
                _usageObserved = true;
                break;
            case ModelCompletedEvent completed:
                FinishReason = completed.FinishReason;
                _terminal = true;
                break;
            case ModelErrorEvent error:
                ProviderError = error.Error with { };
                _terminal = true;
                break;
        }

        return DomainResult.Success(true);
    }

    public DomainResult<ModelRunStreamEvidence> CompleteEvidence()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_terminal)
        {
            return DomainResult.Fail<ModelRunStreamEvidence>(new DomainFailure(
                FailureCode.RecoverableExternalFailure,
                "Model provider stream ended without one typed terminal event.",
                true));
        }

        return DomainResult.Success(FinalizeEvidence());
    }

    public ModelRunStreamEvidence FinalizeEvidence()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var digest = _hash.GetHashAndReset();
        _disposed = true;
        _hash.Dispose();
        return EventCount == 0
            ? ModelRunStreamEvidence.Empty
            : new ModelRunStreamEvidence(
                EventCount,
                EventCount - 1L,
                $"sha256:{Convert.ToHexStringLower(digest)}");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _hash.Dispose();
    }

    private DomainResult<bool> ValidateEvent(ModelStreamEvent modelEvent)
    {
        if (EventCount == 0)
        {
            return modelEvent is ModelStartedEvent started &&
                started.ProviderProfileId == run.Route.ProfileId &&
                string.Equals(started.ProviderType, run.Route.ProviderType, StringComparison.Ordinal) &&
                string.Equals(started.Model, run.Route.Model, StringComparison.Ordinal) &&
                FixedEquals(started.InputHash, run.PreparedInputHash) && IsHash(started.CapabilityEvidenceHash)
                ? DomainResult.Success(true)
                : Invalid("Model provider did not start with exact route and prepared-input evidence.");
        }

        return modelEvent switch
        {
            ModelStartedEvent => Invalid("Model provider emitted more than one started event."),
            ModelTextDeltaEvent text when IsContent(text.Delta, 32_768) => DomainResult.Success(true),
            ModelStructuredOutputEvent structured when
                structured.Json is not null && structured.Json.Length <= 262_144 &&
                ModelContractValidator.TryNormalizeJson(structured.Json, 262_144, out _) =>
                DomainResult.Success(true),
            ModelUsageEvent usage when !_usageObserved && ValidateUsage(usage.Usage) =>
                DomainResult.Success(true),
            ModelCompletedEvent completed when Enum.IsDefined(completed.FinishReason) &&
                completed.FinishReason is not ModelFinishReason.ToolCalls => DomainResult.Success(true),
            ModelErrorEvent error when ValidateError(error.Error) => DomainResult.Success(true),
            ModelToolCallDeltaEvent or ModelToolCallCompletedEvent =>
                DomainResult.Fail<bool>(new DomainFailure(
                    FailureCode.UnsupportedCapability,
                    "Tool-call execution is unavailable at the model attempt boundary.")),
            _ => Invalid("Model provider emitted invalid or unsupported stream evidence."),
        };
    }

    private void Append(ModelStreamEvent modelEvent)
    {
        var canonical = modelEvent switch
        {
            ModelStartedEvent started => new
            {
                Kind = nameof(ModelStartedEvent),
                RequestId = started.RequestId.ToString(),
                started.Sequence,
                started.Timestamp,
                ProviderProfileId = started.ProviderProfileId.ToString(),
                started.ProviderType,
                started.Model,
                started.InputHash,
                started.CapabilityEvidenceHash,
                started.ContextRedactionCount,
                started.ContextPreparationPolicy,
            } as object,
            ModelTextDeltaEvent text => new
            {
                Kind = nameof(ModelTextDeltaEvent),
                RequestId = text.RequestId.ToString(),
                text.Sequence,
                text.Timestamp,
                text.Delta,
            },
            ModelStructuredOutputEvent structured => new
            {
                Kind = nameof(ModelStructuredOutputEvent),
                RequestId = structured.RequestId.ToString(),
                structured.Sequence,
                structured.Timestamp,
                structured.Json,
            },
            ModelUsageEvent usage => new
            {
                Kind = nameof(ModelUsageEvent),
                RequestId = usage.RequestId.ToString(),
                usage.Sequence,
                usage.Timestamp,
                usage.Usage.InputTokens,
                usage.Usage.OutputTokens,
                usage.Usage.ToolCalls,
                usage.Usage.Cost,
                usage.Usage.Currency,
            },
            ModelCompletedEvent completed => new
            {
                Kind = nameof(ModelCompletedEvent),
                RequestId = completed.RequestId.ToString(),
                completed.Sequence,
                completed.Timestamp,
                FinishReason = completed.FinishReason.ToString(),
            },
            ModelErrorEvent error => new
            {
                Kind = nameof(ModelErrorEvent),
                RequestId = error.RequestId.ToString(),
                error.Sequence,
                error.Timestamp,
                Code = error.Error.Code.ToString(),
                error.Error.Message,
                error.Error.IsRetryable,
                error.Error.StatusCode,
                error.Error.RetryAfter,
            },
            _ => throw new InvalidOperationException("Unsupported model event passed structural validation."),
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(canonical, SerializerOptions);
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        _hash.AppendData(length);
        _hash.AppendData(bytes);
    }

    private static bool ValidateUsage(ModelUsage usage) =>
        usage is not null && usage.InputTokens >= 0 && usage.OutputTokens >= 0 && usage.ToolCalls >= 0 &&
        usage.Cost is null or >= 0 and <= 1_000_000_000 &&
        (usage.Cost is null && usage.Currency is null ||
            usage.Cost is not null && usage.Currency is { Length: 3 } currency &&
            currency.All(char.IsAsciiLetter));

    private static bool ValidateError(ModelProviderError error) =>
        error is not null && Enum.IsDefined(error.Code) && IsSingleLine(error.Message, 2048) &&
        error.StatusCode is null or >= 100 and <= 599 &&
        (error.RetryAfter is null || error.RetryAfter >= TimeSpan.Zero &&
            error.RetryAfter <= TimeSpan.FromDays(1));

    private static bool IsContent(string? value, int maximumLength) =>
        value is not null && value.Length <= maximumLength &&
        value.All(character => !char.IsControl(character) || character is '\r' or '\n' or '\t');

    private static bool IsSingleLine(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength &&
        !value.Any(char.IsControl) && string.Equals(value, value.Trim(), StringComparison.Ordinal);

    private static bool IsHash(string? value)
    {
        if (value is not { Length: 71 } || !value.StartsWith("sha256:", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var character in value.AsSpan(7))
        {
            if (!char.IsAsciiDigit(character) && character is not (>= 'a' and <= 'f'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool FixedEquals(string first, string second) =>
        first is not null && second is not null && first.Length == second.Length &&
        CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(first),
            Encoding.ASCII.GetBytes(second));

    private static DomainResult<bool> Invalid(string message) =>
        DomainResult.Fail<bool>(new DomainFailure(
            FailureCode.RecoverableExternalFailure,
            message,
            true));
}
