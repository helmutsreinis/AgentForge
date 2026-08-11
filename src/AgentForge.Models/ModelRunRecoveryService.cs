using AgentForge.Abstractions.Auditing;
using AgentForge.Abstractions.Models;
using AgentForge.Abstractions.Persistence;
using AgentForge.Abstractions.Security;
using AgentForge.Abstractions.Time;
using AgentForge.Domain.Auditing;
using AgentForge.Domain.Models;
using AgentForge.Domain.Primitives;

namespace AgentForge.Models;

internal sealed class ModelRunRecoveryService(
    IModelRunRepository runs,
    IModelBudgetLedgerRepository ledgers,
    IModelProviderHealthRepository health,
    ISensitiveDataRedactor redactor,
    IAuditRecorder auditRecorder,
    IUnitOfWork unitOfWork,
    IClock clock) : IModelRunRecoveryService
{
    public async Task<DomainResult<ModelRunHeartbeatResult>> HeartbeatAsync(
        ModelRunHeartbeatRequest request,
        CancellationToken cancellationToken)
    {
        var validation = ValidateHeartbeat(request);
        if (!validation.IsSuccess)
        {
            return DomainResult.Fail<ModelRunHeartbeatResult>(validation.Failure!);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (redactor.Redact(new
        {
            request.WorkerId,
            CorrelationId = request.CorrelationId.Value,
            CausationId = request.CausationId?.Value,
        }).ContainsRedactions)
        {
            return InvalidHeartbeat("Model run heartbeat metadata cannot contain credential-shaped values.");
        }

        var aggregate = await runs.FindByIdAsync(request.RunId, cancellationToken);
        if (!Matches(aggregate, request.ExpectedRunVersion, request.ExpectedAttemptVersion))
        {
            return ConflictHeartbeat("Model run heartbeat versions do not match durable state.");
        }

        var heartbeat = ModelRunStateMachine.Heartbeat(
            aggregate!,
            request.WorkerId,
            request.LeaseToken,
            clock.UtcNow);
        if (!heartbeat.IsSuccess)
        {
            return DomainResult.Fail<ModelRunHeartbeatResult>(heartbeat.Failure!);
        }

        await runs.UpdateAsync(
            heartbeat.Value,
            request.ExpectedRunVersion,
            request.ExpectedAttemptVersion,
            cancellationToken);
        var commit = await unitOfWork.CommitAsync(cancellationToken);
        return commit.Succeeded
            ? DomainResult.Success(new ModelRunHeartbeatResult(heartbeat.Value))
            : DomainResult.Fail<ModelRunHeartbeatResult>(commit.Failure!);
    }

    public async Task<DomainResult<ModelRunRecoveryResult>> RecoverExpiredAsync(
        ModelRunRecoveryRequest request,
        CancellationToken cancellationToken)
    {
        var validation = ValidateRecovery(request);
        if (!validation.IsSuccess)
        {
            return DomainResult.Fail<ModelRunRecoveryResult>(validation.Failure!);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (redactor.Redact(new
        {
            ActorId = request.ActorId.Value,
            CorrelationId = request.CorrelationId.Value,
            CausationId = request.CausationId?.Value,
        }).ContainsRedactions)
        {
            return InvalidRecovery("Model run recovery metadata cannot contain credential-shaped values.");
        }

        var aggregate = await runs.FindByIdAsync(request.RunId, cancellationToken);
        if (!Matches(aggregate, request.ExpectedRunVersion, request.ExpectedAttemptVersion))
        {
            return ConflictRecovery("Model run recovery versions do not match durable state.");
        }

        var recoveredAt = clock.UtcNow;
        var recovered = ModelRunStateMachine.RecoverExpiredLease(aggregate!, recoveredAt);
        if (!recovered.IsSuccess)
        {
            return DomainResult.Fail<ModelRunRecoveryResult>(recovered.Failure!);
        }

        var currentLedger = await ledgers.FindAsync(recovered.Value.Run.AgentId, cancellationToken);
        if (currentLedger is null)
        {
            return ConflictRecovery("Expired model run has no durable active budget reservation.");
        }

        var reconciled = ModelBudgetLedgerStateMachine.Reconcile(
            currentLedger,
            recovered.Value,
            recoveredAt);
        if (!reconciled.IsSuccess)
        {
            return DomainResult.Fail<ModelRunRecoveryResult>(reconciled.Failure!);
        }

        var currentHealth = await health.FindAsync(recovered.Value.Run.Route.ProfileId, cancellationToken);
        var observed = ModelProviderHealthStateMachine.Observe(
            currentHealth,
            new ModelProviderHealthObservation(
                recovered.Value.Run.InstallationId,
                recovered.Value.Run.Route.ProfileId,
                recovered.Value.Run.Id,
                recovered.Value.Attempt.Id,
                ModelProviderHealthObservationOutcome.LeaseExpired,
                request.ActorId,
                request.CorrelationId,
                request.CausationId,
                recoveredAt));
        if (!observed.IsSuccess)
        {
            return DomainResult.Fail<ModelRunRecoveryResult>(observed.Failure!);
        }

        await runs.UpdateAsync(
            recovered.Value,
            request.ExpectedRunVersion,
            request.ExpectedAttemptVersion,
            cancellationToken);
        await ledgers.UpdateAsync(
            reconciled.Value.Ledger,
            reconciled.Value.ExpectedVersion!.Value,
            cancellationToken);
        await PersistHealthAsync(observed.Value, cancellationToken);
        await RecordRecoveryAsync(
            request,
            recovered.Value,
            reconciled.Value.Ledger,
            observed.Value.Record,
            cancellationToken);

        var commit = await unitOfWork.CommitAsync(cancellationToken);
        return commit.Succeeded
            ? DomainResult.Success(new ModelRunRecoveryResult(recovered.Value, observed.Value.Record))
            : DomainResult.Fail<ModelRunRecoveryResult>(commit.Failure!);
    }

    private async ValueTask PersistHealthAsync(
        ModelProviderHealthMutation mutation,
        CancellationToken cancellationToken)
    {
        if (mutation.IsNew)
        {
            await health.AddAsync(mutation.Record, cancellationToken);
        }
        else
        {
            await health.UpdateAsync(mutation.Record, mutation.ExpectedVersion!.Value, cancellationToken);
        }
    }

    private async Task RecordRecoveryAsync(
        ModelRunRecoveryRequest request,
        ModelRunAggregate aggregate,
        ModelBudgetLedgerRecord ledger,
        ModelProviderHealthRecord healthRecord,
        CancellationToken cancellationToken)
    {
        await auditRecorder.RecordAsync(new AuditRecordRequest(
            aggregate.Run.InstallationId,
            request.ActorId,
            request.CorrelationId,
            request.CausationId,
            "model.run-lease-expired",
            AuditOutcome.Failed,
            new
            {
                RunId = aggregate.Run.Id.ToString(),
                AttemptId = aggregate.Attempt.Id.ToString(),
                LeaseOwner = aggregate.Run.Lease!.Owner,
                aggregate.Run.Lease.TokenHash,
                aggregate.Run.Lease.HeartbeatAt,
                aggregate.Run.Lease.ExpiresAt,
                ExpectedRunVersion = request.ExpectedRunVersion,
                ExpectedAttemptVersion = request.ExpectedAttemptVersion,
            },
            new
            {
                RunState = aggregate.Run.State.ToString(),
                AttemptState = aggregate.Attempt.State.ToString(),
                FailureCode = aggregate.Run.FailureCode?.ToString(),
                aggregate.Run.CompletedAt,
                LedgerVersion = ledger.Version,
                ledger.ActiveRuns,
                HealthStatus = healthRecord.Evidence.Status.ToString(),
                healthRecord.Evidence.EvidenceCode,
                HealthVersion = healthRecord.Version,
            },
            FailureCode.RecoverableExternalFailure.ToString()), cancellationToken);
    }

    private static bool Matches(ModelRunAggregate? aggregate, long runVersion, long attemptVersion) =>
        aggregate is not null && aggregate.Run.Version == runVersion &&
        aggregate.Attempt.Version == attemptVersion;

    private static DomainResult<bool> ValidateHeartbeat(ModelRunHeartbeatRequest request)
    {
        if (request is null || request.RunId.Value == Guid.Empty || request.ExpectedRunVersion < 0 ||
            request.ExpectedAttemptVersion < 0 || !IsBounded(request.WorkerId, 256) ||
            string.IsNullOrWhiteSpace(request.LeaseToken) ||
            !IsBounded(request.CorrelationId.Value, 128) ||
            request.CausationId is { } causation && !IsBounded(causation.Value, 128))
        {
            return DomainResult.Fail<bool>(new DomainFailure(
                FailureCode.ValidationFailure,
                "Model run heartbeat identity, versions, lease, and correlation are invalid."));
        }

        return DomainResult.Success(true);
    }

    private static DomainResult<bool> ValidateRecovery(ModelRunRecoveryRequest request)
    {
        if (request is null || request.RunId.Value == Guid.Empty || request.ExpectedRunVersion < 0 ||
            request.ExpectedAttemptVersion < 0 || !IsBounded(request.ActorId.Value, 256) ||
            !IsBounded(request.CorrelationId.Value, 128) ||
            request.CausationId is { } causation && !IsBounded(causation.Value, 128))
        {
            return DomainResult.Fail<bool>(new DomainFailure(
                FailureCode.ValidationFailure,
                "Model run recovery identity, versions, actor, and correlation are invalid."));
        }

        return DomainResult.Success(true);
    }

    private static bool IsBounded(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength && !value.Any(char.IsControl);

    private static DomainResult<ModelRunHeartbeatResult> InvalidHeartbeat(string message) =>
        DomainResult.Fail<ModelRunHeartbeatResult>(new DomainFailure(FailureCode.ValidationFailure, message));

    private static DomainResult<ModelRunRecoveryResult> InvalidRecovery(string message) =>
        DomainResult.Fail<ModelRunRecoveryResult>(new DomainFailure(FailureCode.ValidationFailure, message));

    private static DomainResult<ModelRunHeartbeatResult> ConflictHeartbeat(string message) =>
        DomainResult.Fail<ModelRunHeartbeatResult>(new DomainFailure(
            FailureCode.ConcurrencyConflict,
            message,
            true));

    private static DomainResult<ModelRunRecoveryResult> ConflictRecovery(string message) =>
        DomainResult.Fail<ModelRunRecoveryResult>(new DomainFailure(
            FailureCode.ConcurrencyConflict,
            message,
            true));
}
