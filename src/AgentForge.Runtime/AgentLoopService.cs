using AgentForge.Abstractions.Auditing;
using AgentForge.Abstractions.Persistence;
using AgentForge.Abstractions.Runtime;
using AgentForge.Abstractions.Time;
using AgentForge.Domain.Auditing;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Runtime;

namespace AgentForge.Runtime;

internal sealed class AgentLoopService(
    IRunSnapshotStore snapshots,
    IAgentLoopStepExecutor executor,
    IAuditRecorder audit,
    IUnitOfWork unitOfWork,
    IClock clock) : IAgentLoopService
{
    public async Task<DomainResult<AgentLoopRunResult>> RunAsync(
        AgentLoopRunRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var current = await snapshots.FindByIdempotencyKeyAsync(
            request.InstallationId,
            request.IdempotencyKey,
            cancellationToken);
        var resumed = current is not null;
        if (current is null)
        {
            var created = AgentLoopStateMachine.Create(
                request.LoopId,
                request.InstallationId,
                request.AgentId,
                request.AgentVersion,
                request.Budget,
                request.InitialStateHash,
                request.ActorId,
                request.IdempotencyKey,
                request.CorrelationId,
                request.CausationId,
                clock.UtcNow);
            if (!created.IsSuccess)
            {
                return DomainResult.Fail<AgentLoopRunResult>(created.Failure!);
            }

            current = created.Value;
            var persisted = await PersistAsync(current, "runtime.loop-created", cancellationToken);
            if (!persisted.IsSuccess)
            {
                return DomainResult.Fail<AgentLoopRunResult>(persisted.Failure!);
            }
        }
        else if (!Matches(request, current))
        {
            return DomainResult.Fail<AgentLoopRunResult>(new DomainFailure(
                FailureCode.ConcurrencyConflict,
                "The idempotency key is already bound to different agent-loop authority or input."));
        }

        if (AgentLoopStateMachine.IsTerminal(current.State))
        {
            return DomainResult.Success(new AgentLoopRunResult(current, true));
        }

        while (!AgentLoopStateMachine.IsTerminal(current.State))
        {
            if (cancellationToken.IsCancellationRequested)
            {
                var canceled = AgentLoopStateMachine.Cancel(
                    current,
                    AgentLoopStateMachine.EmptyHash,
                    AtLeast(clock.UtcNow, current.UpdatedAt));
                if (!canceled.IsSuccess)
                {
                    return DomainResult.Fail<AgentLoopRunResult>(canceled.Failure!);
                }

                current = canceled.Value;
                var persisted = await PersistAsync(current, "runtime.loop-canceled", CancellationToken.None);
                return persisted.IsSuccess
                    ? DomainResult.Success(new AgentLoopRunResult(current, resumed))
                    : DomainResult.Fail<AgentLoopRunResult>(persisted.Failure!);
            }

            DomainResult<AgentLoopStepResult> execution;
            try
            {
                execution = await executor.ExecuteAsync(current, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                continue;
            }

            DomainResult<AgentLoopSnapshot> transition;
            if (execution.IsSuccess)
            {
                transition = AgentLoopStateMachine.Advance(
                    current,
                    execution.Value,
                    AtLeast(clock.UtcNow, current.UpdatedAt));
            }
            else
            {
                transition = AgentLoopStateMachine.Fail(
                    current,
                    execution.Failure!,
                    AgentLoopStateMachine.EmptyHash,
                    AtLeast(clock.UtcNow, current.UpdatedAt));
            }

            if (!transition.IsSuccess)
            {
                return DomainResult.Fail<AgentLoopRunResult>(transition.Failure!);
            }

            current = transition.Value;
            var operation = AgentLoopStateMachine.IsTerminal(current.State)
                ? "runtime.loop-terminal"
                : "runtime.loop-snapshot-appended";
            var stored = await PersistAsync(current, operation, cancellationToken);
            if (!stored.IsSuccess)
            {
                return DomainResult.Fail<AgentLoopRunResult>(stored.Failure!);
            }
        }

        return DomainResult.Success(new AgentLoopRunResult(current, resumed));
    }

    private async Task<DomainResult<bool>> PersistAsync(
        AgentLoopSnapshot snapshot,
        string operation,
        CancellationToken cancellationToken)
    {
        await snapshots.AppendAsync(snapshot, cancellationToken);
        await audit.RecordAsync(new AuditRecordRequest(
            snapshot.InstallationId,
            snapshot.ActorId,
            snapshot.CorrelationId,
            snapshot.CausationId,
            operation,
            ToAuditOutcome(snapshot.State),
            new
            {
                LoopId = snapshot.LoopId.ToString(),
                snapshot.Sequence,
                snapshot.Turn,
                Phase = snapshot.Phase.ToString(),
                snapshot.PreviousSnapshotHash,
            },
            new
            {
                State = snapshot.State.ToString(),
                snapshot.SnapshotHash,
                snapshot.StepEvidenceHash,
                snapshot.Consumption,
                snapshot.StructuredRepairCount,
                snapshot.ConsecutiveNoProgress,
            },
            snapshot.FailureCode?.ToString()), cancellationToken);
        var commit = await unitOfWork.CommitAsync(cancellationToken);
        return commit.Succeeded
            ? DomainResult.Success(true)
            : DomainResult.Fail<bool>(commit.Failure!);
    }

    private static bool Matches(AgentLoopRunRequest request, AgentLoopSnapshot snapshot) =>
        request.LoopId == snapshot.LoopId && request.InstallationId == snapshot.InstallationId &&
        request.AgentId == snapshot.AgentId && request.AgentVersion == snapshot.AgentVersion &&
        request.Budget == snapshot.Budget &&
        string.Equals(request.InitialStateHash, snapshot.InitialStateHash, StringComparison.Ordinal) &&
        request.ActorId == snapshot.ActorId && request.CorrelationId == snapshot.CorrelationId &&
        request.CausationId == snapshot.CausationId;

    private static DateTimeOffset AtLeast(DateTimeOffset candidate, DateTimeOffset minimum) =>
        candidate < minimum ? minimum : candidate;

    private static AuditOutcome ToAuditOutcome(AgentLoopState state) => state switch
    {
        AgentLoopState.Canceled => AuditOutcome.Canceled,
        AgentLoopState.Failed or AgentLoopState.BudgetExceeded or AgentLoopState.NoProgress => AuditOutcome.Failed,
        _ => AuditOutcome.Succeeded,
    };
}
