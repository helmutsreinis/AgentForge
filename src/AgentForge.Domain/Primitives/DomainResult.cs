namespace AgentForge.Domain.Primitives;

public enum FailureCode
{
    InvalidStateTransition,
    ValidationFailure,
    UnsupportedCapability,
    ApprovalRequired,
    PolicyDenied,
    ConcurrencyConflict,
    BudgetExceeded,
    RecoverableExternalFailure,
    NoProgress,
}

public sealed record DomainFailure(
    FailureCode Code,
    string Message,
    bool IsRetryable = false);

public readonly record struct DomainResult<T>
{
    private readonly T? _value;

    private DomainResult(T value)
    {
        IsSuccess = true;
        _value = value;
        Failure = null;
    }

    private DomainResult(DomainFailure failure)
    {
        IsSuccess = false;
        _value = default;
        Failure = failure;
    }

    public bool IsSuccess { get; }

    public T Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException("A failed result does not contain a value.");

    public DomainFailure? Failure { get; }

    internal static DomainResult<T> FromValue(T value) => new(value);

    internal static DomainResult<T> FromFailure(DomainFailure failure) => new(failure);
}

public static class DomainResult
{
    public static DomainResult<T> Success<T>(T value) => DomainResult<T>.FromValue(value);

    public static DomainResult<T> Fail<T>(DomainFailure failure) => DomainResult<T>.FromFailure(failure);
}
