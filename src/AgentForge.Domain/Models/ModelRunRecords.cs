using System.Collections.ObjectModel;
using AgentForge.Domain.Agents;
using AgentForge.Domain.Primitives;

namespace AgentForge.Domain.Models;

public readonly record struct ModelRunId(Guid Value)
{
    public override string ToString() => Value.ToString("D");
}

public readonly record struct ModelRunAttemptId(Guid Value)
{
    public override string ToString() => Value.ToString("D");
}

public enum ModelRunState
{
    Reserved,
    Running,
    Succeeded,
    Failed,
    Canceled,
    BudgetExceeded,
}

public enum ModelRunAttemptState
{
    Planned,
    Started,
    Succeeded,
    Failed,
    Canceled,
}

public sealed record ModelRunBudgetReservation(
    long InputTokens,
    long OutputTokens,
    int ToolCalls,
    int WallClockSeconds);

public sealed record ModelRunRecord(
    ModelRunId Id,
    InstallationId InstallationId,
    long InstallationVersion,
    AgentIdentityId AgentId,
    long AgentVersion,
    long ProviderVersion,
    ModelRequestId RequestId,
    ModelRouteSelection Route,
    string PlanEvidenceHash,
    string PreparedInputHash,
    string HealthEvidenceHash,
    int ContextRedactionCount,
    string ContextPreparationPolicy,
    string AdmissionRequestHash,
    ModelRunBudgetReservation Reservation,
    ModelUsage Usage,
    ModelRunState State,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    ActorId ActorId,
    string IdempotencyKey,
    CorrelationId CorrelationId,
    CorrelationId? CausationId,
    ModelFinishReason? FinishReason,
    FailureCode? FailureCode,
    long Version);

public sealed record ModelRunAttemptRecord(
    ModelRunAttemptId Id,
    ModelRunId RunId,
    int Sequence,
    long ProviderVersion,
    ModelRouteSelection Route,
    string PlanEvidenceHash,
    ModelRunAttemptState State,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    ModelUsage Usage,
    ModelFinishReason? FinishReason,
    FailureCode? FailureCode,
    bool IsRetryable,
    long Version);

public sealed record ModelRunAggregate(
    ModelRunRecord Run,
    ModelRunAttemptRecord Attempt);

public sealed record ModelRunAdmissionRequest(
    ModelRoutePlanningRequest PlanningRequest,
    ActorId ActorId,
    string IdempotencyKey,
    CorrelationId CorrelationId,
    CorrelationId? CausationId = null);

public sealed record ModelRunAdmissionResult(
    ModelRunAggregate Aggregate,
    bool IsIdempotentReplay);

public static class ModelRunStateMachine
{
    public static DomainResult<ModelRunAggregate> Reserve(
        ModelRunId runId,
        ModelRunAttemptId attemptId,
        ModelRoutePlan plan,
        ActorId actorId,
        string admissionRequestHash,
        string idempotencyKey,
        CorrelationId correlationId,
        CorrelationId? causationId,
        DateTimeOffset reservedAt)
    {
        if (runId.Value == Guid.Empty || attemptId.Value == Guid.Empty || plan is null ||
            plan.Route is null || plan.Route.ProfileId.Value == Guid.Empty ||
            plan.Route.RequiredCapabilities is null || plan.Route.RequiredCapabilities.Count == 0 ||
            plan.Route.RequiredCapabilities.Count > Enum.GetValues<ModelCapability>().Length ||
            plan.Route.RequiredCapabilities.Any(capability => !Enum.IsDefined(capability)) ||
            plan.InstallationId.Value == Guid.Empty || plan.InstallationVersion < 1 ||
            plan.AgentId.Value == Guid.Empty || plan.AgentVersion < 1 || plan.ProviderVersion < 1 ||
            plan.RequestId.Value == Guid.Empty || !IsHash(plan.PlanEvidenceHash) ||
            !IsHash(plan.Route.SelectionEvidenceHash) || !IsHash(plan.PreparedInputHash) ||
            !IsHash(plan.HealthEvidenceHash) || !IsHash(admissionRequestHash) ||
            !IsBounded(plan.Route.ProviderType, 64) || !IsBounded(plan.Route.Model, 256) ||
            !IsBounded(plan.ContextPreparationPolicy, 128) || plan.ContextRedactionCount < 0 ||
            plan.ReservedInputTokens is < 0 or > 10_000_000 ||
            plan.ReservedOutputTokens is < 1 or > 1_000_000 ||
            plan.ReservedToolCalls is < 0 or > 1_024 ||
            plan.ReservedWallClockSeconds is < 1 or > 86_400 ||
            plan.ValidUntil <= plan.PlannedAt ||
            plan.ValidUntil - plan.PlannedAt > TimeSpan.FromSeconds(5) ||
            reservedAt < plan.PlannedAt || reservedAt >= plan.ValidUntil ||
            !IsBounded(actorId.Value, 256) || !IsBounded(idempotencyKey, 256) ||
            !IsBounded(correlationId.Value, 128) ||
            causationId is { } causation && !IsBounded(causation.Value, 128))
        {
            return Invalid("Model run reservation requires a current bounded plan and exact identity evidence.");
        }

        var route = plan.Route with
        {
            RequiredCapabilities = new ReadOnlySet<ModelCapability>(
                plan.Route.RequiredCapabilities.ToHashSet()),
        };
        var zeroUsage = new ModelUsage(0, 0, 0, null, null);
        var run = new ModelRunRecord(
            runId,
            plan.InstallationId,
            plan.InstallationVersion,
            plan.AgentId,
            plan.AgentVersion,
            plan.ProviderVersion,
            plan.RequestId,
            route,
            plan.PlanEvidenceHash,
            plan.PreparedInputHash,
            plan.HealthEvidenceHash,
            plan.ContextRedactionCount,
            plan.ContextPreparationPolicy,
            admissionRequestHash,
            new ModelRunBudgetReservation(
                plan.ReservedInputTokens,
                plan.ReservedOutputTokens,
                plan.ReservedToolCalls,
                plan.ReservedWallClockSeconds),
            zeroUsage,
            ModelRunState.Reserved,
            reservedAt,
            null,
            null,
            actorId,
            idempotencyKey,
            correlationId,
            causationId,
            null,
            null,
            0);
        var attempt = new ModelRunAttemptRecord(
            attemptId,
            runId,
            1,
            plan.ProviderVersion,
            route,
            plan.PlanEvidenceHash,
            ModelRunAttemptState.Planned,
            reservedAt,
            null,
            null,
            zeroUsage,
            null,
            null,
            false,
            0);
        return DomainResult.Success(new ModelRunAggregate(run, attempt));
    }

    public static DomainResult<ModelRunAggregate> Start(
        ModelRunAggregate aggregate,
        DateTimeOffset startedAt)
    {
        if (!IsConsistent(aggregate) || aggregate.Run.State is not ModelRunState.Reserved ||
            aggregate.Attempt.State is not ModelRunAttemptState.Planned ||
            startedAt < aggregate.Run.CreatedAt)
        {
            return Invalid("Only one consistent reserved model run attempt can start.");
        }

        return DomainResult.Success(new ModelRunAggregate(
            aggregate.Run with
            {
                State = ModelRunState.Running,
                StartedAt = startedAt,
                Version = checked(aggregate.Run.Version + 1),
            },
            aggregate.Attempt with
            {
                State = ModelRunAttemptState.Started,
                StartedAt = startedAt,
                Version = checked(aggregate.Attempt.Version + 1),
            }));
    }

    public static DomainResult<ModelRunAggregate> Complete(
        ModelRunAggregate aggregate,
        ModelUsage usage,
        ModelFinishReason finishReason,
        DateTimeOffset completedAt)
    {
        if (!CanFinish(aggregate, usage, completedAt) || !Enum.IsDefined(finishReason) ||
            ExceedsReservation(aggregate, usage, completedAt))
        {
            return Invalid("Model completion requires started state and usage within the reserved budget.");
        }

        return Finish(
            aggregate,
            usage,
            completedAt,
            ModelRunState.Succeeded,
            ModelRunAttemptState.Succeeded,
            finishReason,
            null,
            false);
    }

    public static DomainResult<ModelRunAggregate> RecordBudgetExceeded(
        ModelRunAggregate aggregate,
        ModelUsage usage,
        DateTimeOffset completedAt)
    {
        if (!CanFinish(aggregate, usage, completedAt) ||
            !ExceedsReservation(aggregate, usage, completedAt))
        {
            return Invalid("Budget-exceeded completion requires observed usage beyond the reservation.");
        }

        return Finish(
            aggregate,
            usage,
            completedAt,
            ModelRunState.BudgetExceeded,
            ModelRunAttemptState.Failed,
            null,
            FailureCode.BudgetExceeded,
            false);
    }

    public static DomainResult<ModelRunAggregate> Fail(
        ModelRunAggregate aggregate,
        DomainFailure failure,
        ModelUsage usage,
        DateTimeOffset failedAt)
    {
        if (failure is null || failure.Code is FailureCode.BudgetExceeded ||
            !CanFinish(aggregate, usage, failedAt) ||
            ExceedsReservation(aggregate, usage, failedAt))
        {
            return Invalid("Model failure requires started state and bounded observed usage.");
        }

        return Finish(
            aggregate,
            usage,
            failedAt,
            ModelRunState.Failed,
            ModelRunAttemptState.Failed,
            null,
            failure.Code,
            failure.IsRetryable);
    }

    public static DomainResult<ModelRunAggregate> Cancel(
        ModelRunAggregate aggregate,
        DateTimeOffset canceledAt)
    {
        if (!IsConsistent(aggregate) ||
            aggregate.Run.State is not (ModelRunState.Reserved or ModelRunState.Running) ||
            aggregate.Attempt.State is not (ModelRunAttemptState.Planned or ModelRunAttemptState.Started) ||
            canceledAt < aggregate.Run.CreatedAt)
        {
            return Invalid("Only a reserved or running model run can be canceled.");
        }

        return DomainResult.Success(new ModelRunAggregate(
            aggregate.Run with
            {
                State = ModelRunState.Canceled,
                CompletedAt = canceledAt,
                Version = checked(aggregate.Run.Version + 1),
            },
            aggregate.Attempt with
            {
                State = ModelRunAttemptState.Canceled,
                CompletedAt = canceledAt,
                Version = checked(aggregate.Attempt.Version + 1),
            }));
    }

    private static DomainResult<ModelRunAggregate> Finish(
        ModelRunAggregate aggregate,
        ModelUsage usage,
        DateTimeOffset completedAt,
        ModelRunState runState,
        ModelRunAttemptState attemptState,
        ModelFinishReason? finishReason,
        FailureCode? failureCode,
        bool retryable) =>
        DomainResult.Success(new ModelRunAggregate(
            aggregate.Run with
            {
                State = runState,
                CompletedAt = completedAt,
                Usage = usage with { Currency = usage.Currency?.ToUpperInvariant() },
                FinishReason = finishReason,
                FailureCode = failureCode,
                Version = checked(aggregate.Run.Version + 1),
            },
            aggregate.Attempt with
            {
                State = attemptState,
                CompletedAt = completedAt,
                Usage = usage with { Currency = usage.Currency?.ToUpperInvariant() },
                FinishReason = finishReason,
                FailureCode = failureCode,
                IsRetryable = retryable,
                Version = checked(aggregate.Attempt.Version + 1),
            }));

    private static bool CanFinish(
        ModelRunAggregate aggregate,
        ModelUsage usage,
        DateTimeOffset completedAt) =>
        IsConsistent(aggregate) && aggregate.Run.State is ModelRunState.Running &&
        aggregate.Attempt.State is ModelRunAttemptState.Started &&
        aggregate.Run.StartedAt is { } startedAt && completedAt >= startedAt &&
        ValidateUsage(usage);

    private static bool IsConsistent(ModelRunAggregate aggregate) =>
        aggregate is not null && aggregate.Run is not null && aggregate.Attempt is not null &&
        aggregate.Run.Id.Value != Guid.Empty && aggregate.Attempt.Id.Value != Guid.Empty &&
        aggregate.Run.Route is not null && aggregate.Attempt.Route is not null &&
        aggregate.Run.Reservation is not null &&
        aggregate.Run.Route.RequiredCapabilities is not null &&
        aggregate.Attempt.Route.RequiredCapabilities is not null &&
        aggregate.Attempt.RunId == aggregate.Run.Id && aggregate.Attempt.Sequence == 1 &&
        aggregate.Attempt.Route.ProfileId == aggregate.Run.Route.ProfileId &&
        IsHash(aggregate.Attempt.PlanEvidenceHash) && IsHash(aggregate.Run.PlanEvidenceHash) &&
        FixedEquals(aggregate.Attempt.PlanEvidenceHash, aggregate.Run.PlanEvidenceHash);

    private static bool ExceedsReservation(
        ModelRunAggregate aggregate,
        ModelUsage usage,
        DateTimeOffset observedAt) =>
        usage.InputTokens > aggregate.Run.Reservation.InputTokens ||
        usage.OutputTokens > aggregate.Run.Reservation.OutputTokens ||
        usage.ToolCalls > aggregate.Run.Reservation.ToolCalls ||
        aggregate.Run.StartedAt is { } startedAt &&
            observedAt - startedAt > TimeSpan.FromSeconds(aggregate.Run.Reservation.WallClockSeconds);

    private static bool ValidateUsage(ModelUsage usage) =>
        usage is not null && usage.InputTokens >= 0 && usage.OutputTokens >= 0 && usage.ToolCalls >= 0 &&
        usage.Cost is null or >= 0 and <= 1_000_000_000 &&
        (usage.Cost is null && usage.Currency is null ||
            usage.Cost is not null && usage.Currency is { Length: 3 } currency &&
            currency.All(char.IsAsciiLetter));

    private static bool IsHash(string value)
    {
        if (value is null || value.Length != 71 || !value.StartsWith("sha256:", StringComparison.Ordinal))
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
        first.Length == second.Length && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.ASCII.GetBytes(first),
            System.Text.Encoding.ASCII.GetBytes(second));

    private static bool IsBounded(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength && !value.Any(char.IsControl);

    private static DomainResult<ModelRunAggregate> Invalid(string message) =>
        DomainResult.Fail<ModelRunAggregate>(new DomainFailure(FailureCode.InvalidStateTransition, message));
}
