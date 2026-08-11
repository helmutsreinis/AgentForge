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
    int Events,
    int WallClockSeconds);

public sealed record ModelRunLease(
    string Owner,
    string TokenHash,
    DateTimeOffset AcquiredAt,
    DateTimeOffset HeartbeatAt,
    DateTimeOffset ExpiresAt);

public sealed record ModelRunStreamEvidence(
    int EventCount,
    long LastSequence,
    string EventStreamHash)
{
    public const string EmptyHash =
        "sha256:e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    public static ModelRunStreamEvidence Empty { get; } = new(0, -1, EmptyHash);
}

public sealed record ModelRunRecord(
    ModelRunId Id,
    InstallationId InstallationId,
    long InstallationVersion,
    AgentIdentityId AgentId,
    long AgentVersion,
    long ProviderVersion,
    IReadOnlyList<AgentForge.Domain.Providers.ProviderProfileId> AttemptedProfileIds,
    ModelRequestId RequestId,
    ModelRouteSelection Route,
    string PlanEvidenceHash,
    string PreparedInputHash,
    string HealthEvidenceHash,
    int ContextRedactionCount,
    string ContextPreparationPolicy,
    string AdmissionRequestHash,
    ModelRunBudgetReservation Reservation,
    ModelRunLease? Lease,
    ModelRunStreamEvidence StreamEvidence,
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
    ModelRunStreamEvidence StreamEvidence,
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
            plan.AttemptedProfileIds is null || plan.AttemptedProfileIds.Count > 8 ||
            plan.AttemptedProfileIds.Any(item => item.Value == Guid.Empty) ||
            plan.AttemptedProfileIds.Distinct().Count() != plan.AttemptedProfileIds.Count ||
            plan.RequestId.Value == Guid.Empty || !IsHash(plan.PlanEvidenceHash) ||
            !IsHash(plan.Route.SelectionEvidenceHash) || !IsHash(plan.PreparedInputHash) ||
            !IsHash(plan.HealthEvidenceHash) || !IsHash(admissionRequestHash) ||
            !IsBounded(plan.Route.ProviderType, 64) || !IsBounded(plan.Route.Model, 256) ||
            !IsBounded(plan.ContextPreparationPolicy, 128) || plan.ContextRedactionCount < 0 ||
            plan.ReservedInputTokens is < 0 or > 10_000_000 ||
            plan.ReservedOutputTokens is < 1 or > 1_000_000 ||
            plan.ReservedToolCalls is < 0 or > 1_024 ||
            plan.ReservedEvents is < 2 or > 100_000 ||
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
            Array.AsReadOnly(plan.AttemptedProfileIds.ToArray()),
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
                plan.ReservedEvents,
                plan.ReservedWallClockSeconds),
            null,
            ModelRunStreamEvidence.Empty,
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
            ModelRunStreamEvidence.Empty,
            zeroUsage,
            null,
            null,
            false,
            0);
        return DomainResult.Success(new ModelRunAggregate(run, attempt));
    }

    public static DomainResult<ModelRunAggregate> Start(
        ModelRunAggregate aggregate,
        string leaseOwner,
        string leaseToken,
        DateTimeOffset startedAt,
        DateTimeOffset expiresAt)
    {
        if (!IsConsistent(aggregate) || aggregate.Run.State is not ModelRunState.Reserved ||
            aggregate.Attempt.State is not ModelRunAttemptState.Planned ||
            startedAt < aggregate.Run.CreatedAt || !IsBounded(leaseOwner, 256) ||
            !IsLeaseToken(leaseToken) || expiresAt <= startedAt ||
            expiresAt - startedAt >
                TimeSpan.FromSeconds(aggregate.Run.Reservation.WallClockSeconds + 60L))
        {
            return Invalid("Only one consistent reserved model run attempt can start with a bounded lease.");
        }

        var lease = new ModelRunLease(
            leaseOwner,
            HashLeaseToken(leaseToken),
            startedAt,
            startedAt,
            expiresAt);

        return DomainResult.Success(new ModelRunAggregate(
            aggregate.Run with
            {
                State = ModelRunState.Running,
                StartedAt = startedAt,
                Lease = lease,
                Version = checked(aggregate.Run.Version + 1),
            },
            aggregate.Attempt with
            {
                State = ModelRunAttemptState.Started,
                StartedAt = startedAt,
                Version = checked(aggregate.Attempt.Version + 1),
            }));
    }

    public static DomainResult<ModelRunAggregate> Heartbeat(
        ModelRunAggregate aggregate,
        string leaseOwner,
        string leaseToken,
        DateTimeOffset heartbeatAt)
    {
        if (!IsConsistent(aggregate) || aggregate.Run.State is not ModelRunState.Running ||
            aggregate.Attempt.State is not ModelRunAttemptState.Started ||
            aggregate.Run.Lease is not { } lease ||
            !string.Equals(lease.Owner, leaseOwner, StringComparison.Ordinal) ||
            !IsLeaseToken(leaseToken) || !FixedEquals(lease.TokenHash, HashLeaseToken(leaseToken)) ||
            heartbeatAt <= lease.HeartbeatAt || heartbeatAt > lease.ExpiresAt ||
            aggregate.Run.StartedAt is not { } startedAt || heartbeatAt < startedAt)
        {
            return Invalid("Only the exact running lease holder can advance a bounded heartbeat.");
        }

        return DomainResult.Success(new ModelRunAggregate(
            aggregate.Run with
            {
                Lease = lease with { HeartbeatAt = heartbeatAt },
                Version = checked(aggregate.Run.Version + 1),
            },
            aggregate.Attempt));
    }

    public static DomainResult<ModelRunAggregate> RecoverExpiredLease(
        ModelRunAggregate aggregate,
        DateTimeOffset recoveredAt)
    {
        if (!IsConsistent(aggregate) || aggregate.Run.State is not ModelRunState.Running ||
            aggregate.Attempt.State is not ModelRunAttemptState.Started ||
            aggregate.Run.Lease is not { } lease || recoveredAt < lease.ExpiresAt ||
            aggregate.Run.StartedAt is not { } startedAt || lease.ExpiresAt < startedAt ||
            !ValidateUsage(aggregate.Attempt.Usage) ||
            !ValidateStreamEvidence(aggregate.Attempt.StreamEvidence))
        {
            return Invalid("Only an expired running lease can be recovered deterministically.");
        }

        return Finish(
            aggregate,
            aggregate.Attempt.Usage,
            aggregate.Attempt.StreamEvidence,
            lease.ExpiresAt,
            ModelRunState.Failed,
            ModelRunAttemptState.Failed,
            null,
            FailureCode.RecoverableExternalFailure,
            true);
    }

    public static DomainResult<ModelRunAggregate> Complete(
        ModelRunAggregate aggregate,
        string leaseToken,
        ModelUsage usage,
        ModelRunStreamEvidence streamEvidence,
        ModelFinishReason finishReason,
        DateTimeOffset completedAt)
    {
        if (!CanFinish(aggregate, leaseToken, usage, streamEvidence, completedAt) ||
            !Enum.IsDefined(finishReason) ||
            ExceedsReservation(aggregate, usage, completedAt) ||
            streamEvidence.EventCount > aggregate.Run.Reservation.Events)
        {
            return Invalid("Model completion requires started state and usage within the reserved budget.");
        }

        return Finish(
            aggregate,
            usage,
            streamEvidence,
            completedAt,
            ModelRunState.Succeeded,
            ModelRunAttemptState.Succeeded,
            finishReason,
            null,
            false);
    }

    public static DomainResult<ModelRunAggregate> RecordBudgetExceeded(
        ModelRunAggregate aggregate,
        string leaseToken,
        ModelUsage usage,
        ModelRunStreamEvidence streamEvidence,
        DateTimeOffset completedAt,
        bool providerReported = false)
    {
        if (!CanFinish(aggregate, leaseToken, usage, streamEvidence, completedAt) ||
            !ExceedsReservation(aggregate, usage, completedAt) &&
                streamEvidence.EventCount <= aggregate.Run.Reservation.Events && !providerReported)
        {
            return Invalid("Budget-exceeded completion requires observed usage beyond the reservation.");
        }

        return Finish(
            aggregate,
            usage,
            streamEvidence,
            completedAt,
            ModelRunState.BudgetExceeded,
            ModelRunAttemptState.Failed,
            null,
            FailureCode.BudgetExceeded,
            false);
    }

    public static DomainResult<ModelRunAggregate> Fail(
        ModelRunAggregate aggregate,
        string leaseToken,
        DomainFailure failure,
        ModelUsage usage,
        ModelRunStreamEvidence streamEvidence,
        DateTimeOffset failedAt)
    {
        if (failure is null || failure.Code is FailureCode.BudgetExceeded ||
            !CanFinish(aggregate, leaseToken, usage, streamEvidence, failedAt) ||
            ExceedsReservation(aggregate, usage, failedAt) ||
            streamEvidence.EventCount > aggregate.Run.Reservation.Events)
        {
            return Invalid("Model failure requires started state and bounded observed usage.");
        }

        return Finish(
            aggregate,
            usage,
            streamEvidence,
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
            aggregate.Run.State is not ModelRunState.Reserved ||
            aggregate.Attempt.State is not ModelRunAttemptState.Planned ||
            canceledAt < aggregate.Run.CreatedAt)
        {
            return Invalid("Only a reserved model run can be canceled without its lease token.");
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

    public static DomainResult<ModelRunAggregate> CancelRunning(
        ModelRunAggregate aggregate,
        string leaseToken,
        ModelUsage usage,
        ModelRunStreamEvidence streamEvidence,
        DateTimeOffset canceledAt)
    {
        if (!CanFinish(aggregate, leaseToken, usage, streamEvidence, canceledAt) ||
            ExceedsReservation(aggregate, usage, canceledAt) ||
            streamEvidence.EventCount > aggregate.Run.Reservation.Events)
        {
            return Invalid("Only the exact running lease holder can cancel with bounded evidence.");
        }

        return Finish(
            aggregate,
            usage,
            streamEvidence,
            canceledAt,
            ModelRunState.Canceled,
            ModelRunAttemptState.Canceled,
            null,
            null,
            false);
    }

    private static DomainResult<ModelRunAggregate> Finish(
        ModelRunAggregate aggregate,
        ModelUsage usage,
        ModelRunStreamEvidence streamEvidence,
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
                StreamEvidence = streamEvidence with { },
                Usage = usage with { Currency = usage.Currency?.ToUpperInvariant() },
                FinishReason = finishReason,
                FailureCode = failureCode,
                Version = checked(aggregate.Run.Version + 1),
            },
            aggregate.Attempt with
            {
                State = attemptState,
                CompletedAt = completedAt,
                StreamEvidence = streamEvidence with { },
                Usage = usage with { Currency = usage.Currency?.ToUpperInvariant() },
                FinishReason = finishReason,
                FailureCode = failureCode,
                IsRetryable = retryable,
                Version = checked(aggregate.Attempt.Version + 1),
            }));

    private static bool CanFinish(
        ModelRunAggregate aggregate,
        string leaseToken,
        ModelUsage usage,
        ModelRunStreamEvidence streamEvidence,
        DateTimeOffset completedAt) =>
        IsConsistent(aggregate) && aggregate.Run.State is ModelRunState.Running &&
        aggregate.Attempt.State is ModelRunAttemptState.Started &&
        aggregate.Run.Lease is { } lease && IsLeaseToken(leaseToken) &&
        FixedEquals(lease.TokenHash, HashLeaseToken(leaseToken)) &&
        aggregate.Run.StartedAt is { } startedAt && completedAt >= startedAt &&
        completedAt <= lease.ExpiresAt &&
        ValidateUsage(usage) && ValidateStreamEvidence(streamEvidence);

    private static bool IsConsistent(ModelRunAggregate aggregate) =>
        aggregate is not null && aggregate.Run is not null && aggregate.Attempt is not null &&
        aggregate.Run.Id.Value != Guid.Empty && aggregate.Attempt.Id.Value != Guid.Empty &&
        aggregate.Run.Route is not null && aggregate.Attempt.Route is not null &&
        aggregate.Run.Reservation is not null &&
        aggregate.Run.StreamEvidence is not null && aggregate.Attempt.StreamEvidence is not null &&
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
            observedAt - startedAt >= TimeSpan.FromSeconds(aggregate.Run.Reservation.WallClockSeconds);

    private static bool ValidateUsage(ModelUsage usage) =>
        usage is not null && usage.InputTokens >= 0 && usage.OutputTokens >= 0 && usage.ToolCalls >= 0 &&
        usage.Cost is null or >= 0 and <= 1_000_000_000 &&
        (usage.Cost is null && usage.Currency is null ||
            usage.Cost is not null && usage.Currency is { Length: 3 } currency &&
            currency.All(char.IsAsciiLetter));

    private static bool ValidateStreamEvidence(ModelRunStreamEvidence evidence) =>
        evidence is not null && evidence.EventCount is >= 0 and <= 100_001 &&
        (evidence.EventCount == 0 && evidence.LastSequence == -1 &&
                FixedEquals(evidence.EventStreamHash, ModelRunStreamEvidence.EmptyHash) ||
            evidence.EventCount > 0 && evidence.LastSequence == evidence.EventCount - 1L &&
                IsHash(evidence.EventStreamHash));

    private static bool IsLeaseToken(string? value) =>
        value is { Length: 43 } && value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private static string HashLeaseToken(string value) =>
        $"sha256:{Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.ASCII.GetBytes(value)))}";

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
