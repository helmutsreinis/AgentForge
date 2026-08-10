using AgentForge.Domain.Primitives;

namespace AgentForge.Domain.Persistence;

public sealed record CommitResult(
    bool Succeeded,
    int AffectedRows,
    DomainFailure? Failure)
{
    public static CommitResult Success(int affectedRows) => new(true, affectedRows, null);

    public static CommitResult ConcurrencyConflict(string message) => new(
        false,
        0,
        new DomainFailure(FailureCode.ConcurrencyConflict, message, IsRetryable: true));
}
