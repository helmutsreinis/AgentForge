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
    int MaximumAttempts,
    int ConsumedWallClockSeconds,
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
    ModelRunBudgetReservation Reservation,
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
    CorrelationId? CausationId = null,
    int MaximumAttempts = 1);

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
        DateTimeOffset reservedAt,
        int maximumAttempts = 1)
    {
        if (!TryMultiply(plan?.ReservedInputTokens ?? -1, maximumAttempts, out var totalInput) ||
            !TryMultiply(plan?.ReservedOutputTokens ?? -1, maximumAttempts, out var totalOutput) ||
            !TryMultiply(plan?.ReservedToolCalls ?? -1, maximumAttempts, out var totalTools) ||
            !TryMultiply(plan?.ReservedEvents ?? -1, maximumAttempts, out var totalEvents) ||
            !TryMultiply(plan?.ReservedWallClockSeconds ?? -1, maximumAttempts, out var totalWallClock))
        {
            return Invalid("Model run retry budget exceeds durable bounds.");
        }

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
            maximumAttempts is < 1 or > 8 || plan.AttemptedProfileIds.Count != 0 ||
            totalInput > 10_000_000 || totalOutput > 1_000_000 || totalTools > 1_024 ||
            totalEvents > 100_000 || totalWallClock > 86_400 ||
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
            new ModelRunBudgetReservation(totalInput, totalOutput, totalTools, totalEvents, totalWallClock),
            maximumAttempts,
            0,
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
            new ModelRunBudgetReservation(
                plan.ReservedInputTokens,
                plan.ReservedOutputTokens,
                plan.ReservedToolCalls,
                plan.ReservedEvents,
                plan.ReservedWallClockSeconds),
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
                TimeSpan.FromSeconds(aggregate.Attempt.Reservation.WallClockSeconds + 60L))
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
                StartedAt = aggregate.Run.StartedAt ?? startedAt,
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

    public static DomainResult<ModelRunAggregate> Retry(
        ModelRunAggregate aggregate,
        ModelRunAttemptId attemptId,
        ModelRoutePlan plan,
        DateTimeOffset plannedAt)
    {
        if (!IsConsistent(aggregate) || aggregate.Run.State is not ModelRunState.Failed ||
            aggregate.Attempt.State is not ModelRunAttemptState.Failed ||
            !aggregate.Attempt.IsRetryable ||
            aggregate.Run.FailureCode is not FailureCode.RecoverableExternalFailure ||
            aggregate.Run.CompletedAt is not { } completedAt || plannedAt < completedAt ||
            attemptId.Value == Guid.Empty || plan is null || plan.Route is null ||
            aggregate.Attempt.Sequence >= aggregate.Run.MaximumAttempts ||
            aggregate.Run.MaximumAttempts is < 1 or > 8 ||
            plan.RequestId != aggregate.Run.RequestId ||
            plan.InstallationId != aggregate.Run.InstallationId ||
            plan.InstallationVersion != aggregate.Run.InstallationVersion ||
            plan.AgentId != aggregate.Run.AgentId || plan.AgentVersion != aggregate.Run.AgentVersion ||
            plan.ProviderVersion < 1 || plan.Route.RequiredCapabilities is null ||
            plan.Route.RequiredCapabilities.Count == 0 ||
            plan.Route.RequiredCapabilities.Count > Enum.GetValues<ModelCapability>().Length ||
            plan.Route.RequiredCapabilities.Any(capability => !Enum.IsDefined(capability)) ||
            plan.AttemptedProfileIds.Count != aggregate.Attempt.Sequence ||
            !plan.AttemptedProfileIds.Take(plan.AttemptedProfileIds.Count - 1)
                .SequenceEqual(aggregate.Run.AttemptedProfileIds) ||
            plan.AttemptedProfileIds[^1] != aggregate.Attempt.Route.ProfileId ||
            plan.AttemptedProfileIds.Distinct().Count() != plan.AttemptedProfileIds.Count ||
            plan.AttemptedProfileIds.Any(item => item.Value == Guid.Empty) ||
            plan.Route.ProfileId.Value == Guid.Empty ||
            plan.AttemptedProfileIds.Contains(plan.Route.ProfileId) ||
            plan.ValidUntil <= plan.PlannedAt ||
            plan.ValidUntil - plan.PlannedAt > TimeSpan.FromSeconds(5) ||
            plannedAt < plan.PlannedAt || plannedAt >= plan.ValidUntil ||
            !IsHash(plan.PlanEvidenceHash) || !IsHash(plan.PreparedInputHash) ||
            !IsHash(plan.HealthEvidenceHash) || !IsHash(plan.Route.SelectionEvidenceHash) ||
            !IsBounded(plan.Route.ProviderType, 64) || !IsBounded(plan.Route.Model, 256) ||
            !IsBounded(plan.ContextPreparationPolicy, 128) || plan.ContextRedactionCount < 0 ||
            plan.ReservedInputTokens is < 0 or > 10_000_000 ||
            plan.ReservedOutputTokens is < 1 or > 1_000_000 ||
            plan.ReservedToolCalls is < 0 or > 1_024 || plan.ReservedEvents is < 2 or > 100_000 ||
            plan.ReservedWallClockSeconds is < 1 or > 86_400 ||
            !FitsRemainingReservation(aggregate.Run, plan))
        {
            return Invalid("Retry requires exact failed-attempt history and remaining bounded authority.");
        }

        var reservation = new ModelRunBudgetReservation(
            plan.ReservedInputTokens,
            plan.ReservedOutputTokens,
            plan.ReservedToolCalls,
            plan.ReservedEvents,
            plan.ReservedWallClockSeconds);
        var route = plan.Route with
        {
            RequiredCapabilities = new ReadOnlySet<ModelCapability>(
                plan.Route.RequiredCapabilities.ToHashSet()),
        };
        var attempt = new ModelRunAttemptRecord(
            attemptId,
            aggregate.Run.Id,
            aggregate.Attempt.Sequence + 1,
            plan.ProviderVersion,
            route,
            plan.PlanEvidenceHash,
            reservation,
            ModelRunAttemptState.Planned,
            plannedAt,
            null,
            null,
            ModelRunStreamEvidence.Empty,
            new ModelUsage(0, 0, 0, null, null),
            null,
            null,
            false,
            0);
        var run = aggregate.Run with
        {
            ProviderVersion = plan.ProviderVersion,
            AttemptedProfileIds = Array.AsReadOnly(plan.AttemptedProfileIds.ToArray()),
            Route = route,
            PlanEvidenceHash = plan.PlanEvidenceHash,
            PreparedInputHash = plan.PreparedInputHash,
            HealthEvidenceHash = plan.HealthEvidenceHash,
            ContextRedactionCount = plan.ContextRedactionCount,
            ContextPreparationPolicy = plan.ContextPreparationPolicy,
            Lease = null,
            State = ModelRunState.Reserved,
            CompletedAt = null,
            FinishReason = null,
            FailureCode = null,
            Version = checked(aggregate.Run.Version + 1),
        };
        return DomainResult.Success(new ModelRunAggregate(run, attempt));
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
            streamEvidence.EventCount > aggregate.Attempt.Reservation.Events ||
            aggregate.Run.StreamEvidence.EventCount >
                aggregate.Run.Reservation.Events - streamEvidence.EventCount)
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
                streamEvidence.EventCount <= aggregate.Attempt.Reservation.Events &&
                aggregate.Run.StreamEvidence.EventCount <=
                    aggregate.Run.Reservation.Events - streamEvidence.EventCount && !providerReported)
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
            streamEvidence.EventCount > aggregate.Attempt.Reservation.Events ||
            aggregate.Run.StreamEvidence.EventCount >
                aggregate.Run.Reservation.Events - streamEvidence.EventCount)
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
            streamEvidence.EventCount > aggregate.Attempt.Reservation.Events ||
            aggregate.Run.StreamEvidence.EventCount >
                aggregate.Run.Reservation.Events - streamEvidence.EventCount)
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
        bool retryable)
    {
        var totalUsage = AddUsage(aggregate.Run.Usage, usage);
        if (!totalUsage.IsSuccess || !TryAdd(
                aggregate.Run.ConsumedWallClockSeconds,
                AttemptElapsedSeconds(aggregate.Attempt, completedAt),
                out var wallClock) ||
            !TryCombineStreamEvidence(
                aggregate.Run.StreamEvidence,
                streamEvidence,
                out var totalStreamEvidence))
        {
            return Invalid("Model terminal evidence exceeds cumulative run accounting bounds.");
        }

        return DomainResult.Success(new ModelRunAggregate(
            aggregate.Run with
            {
                State = runState,
                CompletedAt = completedAt,
                StreamEvidence = totalStreamEvidence,
                Usage = totalUsage.Value,
                ConsumedWallClockSeconds = wallClock,
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
    }

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
        aggregate.Attempt.StartedAt is { } startedAt && completedAt >= startedAt &&
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
        aggregate.Attempt.Reservation is not null && aggregate.Run.MaximumAttempts is >= 1 and <= 8 &&
        aggregate.Run.ConsumedWallClockSeconds >= 0 &&
        aggregate.Attempt.RunId == aggregate.Run.Id && aggregate.Attempt.Sequence is >= 1 and <= 8 &&
        aggregate.Attempt.Sequence <= aggregate.Run.MaximumAttempts &&
        aggregate.Run.AttemptedProfileIds.Count == aggregate.Attempt.Sequence - 1 &&
        aggregate.Run.AttemptedProfileIds.Distinct().Count() == aggregate.Run.AttemptedProfileIds.Count &&
        !aggregate.Run.AttemptedProfileIds.Contains(aggregate.Attempt.Route.ProfileId) &&
        aggregate.Attempt.Route.ProfileId == aggregate.Run.Route.ProfileId &&
        IsHash(aggregate.Attempt.PlanEvidenceHash) && IsHash(aggregate.Run.PlanEvidenceHash) &&
        FixedEquals(aggregate.Attempt.PlanEvidenceHash, aggregate.Run.PlanEvidenceHash);

    private static bool ExceedsReservation(
        ModelRunAggregate aggregate,
        ModelUsage usage,
        DateTimeOffset observedAt) =>
        usage.InputTokens > aggregate.Attempt.Reservation.InputTokens ||
        usage.OutputTokens > aggregate.Attempt.Reservation.OutputTokens ||
        usage.ToolCalls > aggregate.Attempt.Reservation.ToolCalls ||
        aggregate.Run.Usage.InputTokens > aggregate.Run.Reservation.InputTokens - usage.InputTokens ||
        aggregate.Run.Usage.OutputTokens > aggregate.Run.Reservation.OutputTokens - usage.OutputTokens ||
        aggregate.Run.Usage.ToolCalls > aggregate.Run.Reservation.ToolCalls - usage.ToolCalls ||
        aggregate.Attempt.StartedAt is { } startedAt &&
            observedAt - startedAt >= TimeSpan.FromSeconds(aggregate.Attempt.Reservation.WallClockSeconds) ||
        aggregate.Run.ConsumedWallClockSeconds >
            aggregate.Run.Reservation.WallClockSeconds - AttemptElapsedSeconds(aggregate.Attempt, observedAt);

    private static bool FitsRemainingReservation(ModelRunRecord run, ModelRoutePlan plan) =>
        run.Usage.InputTokens <= run.Reservation.InputTokens - plan.ReservedInputTokens &&
        run.Usage.OutputTokens <= run.Reservation.OutputTokens - plan.ReservedOutputTokens &&
        run.Usage.ToolCalls <= run.Reservation.ToolCalls - plan.ReservedToolCalls &&
        run.StreamEvidence.EventCount <= run.Reservation.Events - plan.ReservedEvents &&
        run.ConsumedWallClockSeconds <= run.Reservation.WallClockSeconds - plan.ReservedWallClockSeconds;

    private static DomainResult<ModelUsage> AddUsage(ModelUsage current, ModelUsage observed)
    {
        if (!ValidateUsage(current) || !ValidateUsage(observed) ||
            current.Cost is not null && observed.Cost is not null &&
                !string.Equals(current.Currency, observed.Currency, StringComparison.OrdinalIgnoreCase) ||
            !TryAdd(current.InputTokens, observed.InputTokens, out var input) ||
            !TryAdd(current.OutputTokens, observed.OutputTokens, out var output) ||
            !TryAdd(current.ToolCalls, observed.ToolCalls, out var tools))
        {
            return DomainResult.Fail<ModelUsage>(new DomainFailure(
                FailureCode.InvalidStateTransition,
                "Cross-attempt model usage is invalid or uses incompatible currencies."));
        }

        decimal? cost = null;
        string? currency = null;
        if (current.Cost is not null || observed.Cost is not null)
        {
            cost = (current.Cost ?? 0) + (observed.Cost ?? 0);
            if (cost > 1_000_000_000)
            {
                return DomainResult.Fail<ModelUsage>(new DomainFailure(
                    FailureCode.InvalidStateTransition,
                    "Cross-attempt model cost exceeds its durable bound."));
            }

            currency = (current.Currency ?? observed.Currency)!.ToUpperInvariant();
        }

        return DomainResult.Success(new ModelUsage(input, output, tools, cost, currency));
    }

    private static bool TryCombineStreamEvidence(
        ModelRunStreamEvidence current,
        ModelRunStreamEvidence observed,
        out ModelRunStreamEvidence combined)
    {
        combined = ModelRunStreamEvidence.Empty;
        if (!ValidateStreamEvidence(current) || !ValidateStreamEvidence(observed) ||
            !TryAdd(current.EventCount, observed.EventCount, out var count) || count > 100_001)
        {
            return false;
        }

        if (current.EventCount == 0)
        {
            combined = observed with { };
            return true;
        }

        if (observed.EventCount == 0)
        {
            combined = current with { };
            return true;
        }

        var bytes = System.Text.Encoding.ASCII.GetBytes(current.EventStreamHash + observed.EventStreamHash);
        combined = new ModelRunStreamEvidence(
            count,
            count - 1L,
            $"sha256:{Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes))}");
        return true;
    }

    private static int AttemptElapsedSeconds(ModelRunAttemptRecord attempt, DateTimeOffset observedAt) =>
        attempt.StartedAt is not { } startedAt
            ? 0
            : checked((int)Math.Ceiling((observedAt - startedAt).TotalSeconds));

    private static bool TryAdd(long first, long second, out long result)
    {
        result = 0;
        if (first < 0 || second < 0 || first > long.MaxValue - second)
        {
            return false;
        }

        result = first + second;
        return true;
    }

    private static bool TryAdd(int first, int second, out int result)
    {
        result = 0;
        if (first < 0 || second < 0 || first > int.MaxValue - second)
        {
            return false;
        }

        result = first + second;
        return true;
    }

    private static bool TryMultiply(long value, int multiplier, out long result)
    {
        result = 0;
        if (value < 0 || multiplier < 1 || value > long.MaxValue / multiplier)
        {
            return false;
        }

        result = value * multiplier;
        return true;
    }

    private static bool TryMultiply(int value, int multiplier, out int result)
    {
        result = 0;
        if (value < 0 || multiplier < 1 || value > int.MaxValue / multiplier)
        {
            return false;
        }

        result = value * multiplier;
        return true;
    }

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
