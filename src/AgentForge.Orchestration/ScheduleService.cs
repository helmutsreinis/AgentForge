using System.Security.Cryptography;
using System.Text;
using AgentForge.Abstractions.Auditing;
using AgentForge.Abstractions.Persistence;
using AgentForge.Abstractions.Scheduling;
using AgentForge.Domain.Auditing;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Scheduling;

namespace AgentForge.Orchestration;

internal sealed class ScheduleService(
    IScheduleSnapshotStore snapshots,
    ITimeZoneResolver timeZones,
    IAuditRecorder audit,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider) : IScheduleService
{
    public async Task<DomainResult<ScheduleTransitionResult>> CreateAsync(
        ScheduleDefinition definition,
        ActorId actorId,
        string idempotencyKey,
        CorrelationId correlationId,
        CorrelationId? causationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var zone = timeZones.Resolve(definition.TimeZoneId);
        if (!zone.IsSuccess)
        {
            return DomainResult.Fail<ScheduleTransitionResult>(zone.Failure!);
        }

        var existing = await snapshots.FindByIdempotencyKeyAsync(
            definition.InstallationId,
            idempotencyKey,
            cancellationToken);
        if (existing is not null)
        {
            var replay = ScheduleStateMachine.Create(
                definition,
                zone.Value,
                actorId,
                idempotencyKey,
                correlationId,
                causationId,
                existing.CreatedAt);
            var history = existing.Definition.Id == definition.Id
                ? await snapshots.ListAsync(definition.Id, cancellationToken)
                : [];
            return replay.IsSuccess && history.Count > 0 && string.Equals(
                    replay.Value.SnapshotHash,
                    history[0].SnapshotHash,
                    StringComparison.Ordinal)
                ? DomainResult.Success(new ScheduleTransitionResult(existing, true))
                : Conflict<ScheduleTransitionResult>("Schedule idempotency is bound to different authority or recurrence.");
        }

        var created = ScheduleStateMachine.Create(
            definition,
            zone.Value,
            actorId,
            idempotencyKey,
            correlationId,
            causationId,
            timeProvider.GetUtcNow());
        return created.IsSuccess
            ? await PersistAsync(created.Value, "scheduling.schedule-created", cancellationToken)
            : DomainResult.Fail<ScheduleTransitionResult>(created.Failure!);
    }

    public DomainResult<IReadOnlyList<DateTimeOffset>> Preview(
        ScheduleDefinition definition,
        DateTimeOffset after,
        int count)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var zone = timeZones.Resolve(definition.TimeZoneId);
        return zone.IsSuccess
            ? ScheduleCalculator.Preview(definition, after, count, zone.Value)
            : DomainResult.Fail<IReadOnlyList<DateTimeOffset>>(zone.Failure!);
    }

    public Task<DomainResult<ScheduleTransitionResult>> EvaluateDueAsync(
        ScheduleId scheduleId,
        long expectedVersion,
        CancellationToken cancellationToken) => MutateAsync(
            scheduleId,
            expectedVersion,
            current => WithZone(current, zone =>
                ScheduleStateMachine.EvaluateDue(current, zone, timeProvider.GetUtcNow())),
            "scheduling.due-evaluated",
            cancellationToken);

    public async Task<DomainResult<ScheduleTransitionResult>> RunNowAsync(
        ScheduleId scheduleId,
        long expectedVersion,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 256 ||
            idempotencyKey.Any(char.IsControl))
        {
            return Invalid<ScheduleTransitionResult>("Run-now idempotency is invalid.");
        }

        var current = await ReadCurrentAsync(scheduleId, expectedVersion, cancellationToken);
        if (!current.IsSuccess)
        {
            return DomainResult.Fail<ScheduleTransitionResult>(current.Failure!);
        }

        var hash = Hash($"{current.Value.Definition.InstallationId}:{scheduleId}:{idempotencyKey}");
        var history = await snapshots.ListAsync(scheduleId, cancellationToken);
        if (history.Any(snapshot => snapshot.Occurrences.Any(item =>
                string.Equals(item.IdempotencyKeyHash, hash, StringComparison.Ordinal)) ||
            string.Equals(snapshot.LastOccurrenceIdHash, hash, StringComparison.Ordinal)))
        {
            return DomainResult.Success(new ScheduleTransitionResult(current.Value, true));
        }

        var next = ScheduleStateMachine.RunNow(current.Value, hash, timeProvider.GetUtcNow());
        return next.IsSuccess
            ? await PersistAsync(next.Value, "scheduling.run-now", cancellationToken)
            : DomainResult.Fail<ScheduleTransitionResult>(next.Failure!);
    }

    public async Task<DomainResult<ScheduleLeaseGrant>> ClaimAsync(
        ScheduleId scheduleId,
        long expectedVersion,
        string occurrenceIdHash,
        string owner,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        if (leaseDuration <= TimeSpan.Zero || leaseDuration > TimeSpan.FromMinutes(5))
        {
            return Invalid<ScheduleLeaseGrant>("Schedule leases must be positive and at most five minutes.");
        }

        var current = await ReadCurrentAsync(scheduleId, expectedVersion, cancellationToken);
        if (!current.IsSuccess)
        {
            return DomainResult.Fail<ScheduleLeaseGrant>(current.Failure!);
        }

        Span<byte> random = stackalloc byte[32];
        RandomNumberGenerator.Fill(random);
        var token = Convert.ToHexStringLower(random);
        CryptographicOperations.ZeroMemory(random);
        var now = timeProvider.GetUtcNow();
        var next = ScheduleStateMachine.Claim(
            current.Value,
            occurrenceIdHash,
            owner,
            Hash(token),
            now.Add(leaseDuration),
            now);
        if (!next.IsSuccess)
        {
            return DomainResult.Fail<ScheduleLeaseGrant>(next.Failure!);
        }

        var persisted = await PersistAsync(next.Value, "scheduling.occurrence-claimed", cancellationToken);
        return persisted.IsSuccess
            ? DomainResult.Success(new ScheduleLeaseGrant(
                persisted.Value.Snapshot,
                occurrenceIdHash,
                token,
                persisted.Value.Snapshot.Occurrences.Single(item =>
                    string.Equals(item.IdempotencyKeyHash, occurrenceIdHash, StringComparison.Ordinal)).Lease!.ExpiresAt))
            : DomainResult.Fail<ScheduleLeaseGrant>(persisted.Failure!);
    }

    public Task<DomainResult<ScheduleTransitionResult>> CompleteAsync(
        ScheduleId scheduleId,
        long expectedVersion,
        string occurrenceIdHash,
        string owner,
        string leaseToken,
        string evidenceHash,
        CancellationToken cancellationToken) => MutateAsync(
            scheduleId,
            expectedVersion,
            current => ScheduleStateMachine.Complete(
                current,
                occurrenceIdHash,
                owner,
                Hash(leaseToken),
                evidenceHash,
                timeProvider.GetUtcNow()),
            "scheduling.occurrence-completed",
            cancellationToken);

    public Task<DomainResult<ScheduleTransitionResult>> FailAsync(
        ScheduleId scheduleId,
        long expectedVersion,
        string occurrenceIdHash,
        string owner,
        string leaseToken,
        string evidenceHash,
        CancellationToken cancellationToken) => MutateAsync(
            scheduleId,
            expectedVersion,
            current => ScheduleStateMachine.Fail(
                current,
                occurrenceIdHash,
                owner,
                Hash(leaseToken),
                evidenceHash,
                timeProvider.GetUtcNow()),
            "scheduling.occurrence-failed",
            cancellationToken);

    public Task<DomainResult<ScheduleTransitionResult>> RecoverExpiredAsync(
        ScheduleId scheduleId,
        long expectedVersion,
        CancellationToken cancellationToken) => MutateAsync(
            scheduleId,
            expectedVersion,
            current => ScheduleStateMachine.RecoverExpired(current, timeProvider.GetUtcNow()),
            "scheduling.lease-recovered",
            cancellationToken);

    public Task<DomainResult<ScheduleTransitionResult>> PauseAsync(
        ScheduleId scheduleId,
        long expectedVersion,
        CancellationToken cancellationToken) => MutateAsync(
            scheduleId,
            expectedVersion,
            current => ScheduleStateMachine.Pause(current, timeProvider.GetUtcNow()),
            "scheduling.schedule-paused",
            cancellationToken);

    public Task<DomainResult<ScheduleTransitionResult>> ResumeAsync(
        ScheduleId scheduleId,
        long expectedVersion,
        CancellationToken cancellationToken) => MutateAsync(
            scheduleId,
            expectedVersion,
            current => WithZone(current, zone =>
                ScheduleStateMachine.Resume(current, zone, timeProvider.GetUtcNow())),
            "scheduling.schedule-resumed",
            cancellationToken);

    private async Task<DomainResult<ScheduleTransitionResult>> MutateAsync(
        ScheduleId scheduleId,
        long expectedVersion,
        Func<ScheduleSnapshot, DomainResult<ScheduleSnapshot>> transition,
        string operation,
        CancellationToken cancellationToken)
    {
        var current = await ReadCurrentAsync(scheduleId, expectedVersion, cancellationToken);
        if (!current.IsSuccess)
        {
            return DomainResult.Fail<ScheduleTransitionResult>(current.Failure!);
        }

        var next = transition(current.Value);
        return next.IsSuccess
            ? await PersistAsync(next.Value, operation, cancellationToken)
            : DomainResult.Fail<ScheduleTransitionResult>(next.Failure!);
    }

    private async Task<DomainResult<ScheduleSnapshot>> ReadCurrentAsync(
        ScheduleId scheduleId,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        var current = await snapshots.FindLatestAsync(scheduleId, cancellationToken);
        return current is null
            ? DomainResult.Fail<ScheduleSnapshot>(new DomainFailure(
                FailureCode.ValidationFailure,
                "The schedule does not exist."))
            : current.Version != expectedVersion
                ? DomainResult.Fail<ScheduleSnapshot>(new DomainFailure(
                    FailureCode.ConcurrencyConflict,
                    "The schedule version is stale."))
                : DomainResult.Success(current);
    }

    private DomainResult<ScheduleSnapshot> WithZone(
        ScheduleSnapshot current,
        Func<TimeZoneInfo, DomainResult<ScheduleSnapshot>> operation)
    {
        var zone = timeZones.Resolve(current.Definition.TimeZoneId);
        return zone.IsSuccess ? operation(zone.Value) : DomainResult.Fail<ScheduleSnapshot>(zone.Failure!);
    }

    private async Task<DomainResult<ScheduleTransitionResult>> PersistAsync(
        ScheduleSnapshot snapshot,
        string operation,
        CancellationToken cancellationToken)
    {
        await snapshots.AppendAsync(snapshot, cancellationToken);
        await audit.RecordAsync(new AuditRecordRequest(
            snapshot.Definition.InstallationId,
            snapshot.ActorId,
            snapshot.CorrelationId,
            snapshot.CausationId,
            operation,
            snapshot.State is ScheduleState.DeadLettered ? AuditOutcome.Failed : AuditOutcome.Succeeded,
            new
            {
                ScheduleId = snapshot.Definition.Id.ToString(),
                snapshot.Version,
                snapshot.PreviousSnapshotHash,
            },
            new
            {
                State = snapshot.State.ToString(),
                snapshot.SnapshotHash,
                snapshot.NextScheduledFor,
                snapshot.NextDueAt,
                QueueCount = snapshot.Occurrences.Count,
                snapshot.CompletedCount,
                snapshot.FailedCount,
                snapshot.SkippedCount,
                snapshot.ConsecutiveFailures,
            },
            null), cancellationToken);
        var commit = await unitOfWork.CommitAsync(cancellationToken);
        return commit.Succeeded
            ? DomainResult.Success(new ScheduleTransitionResult(snapshot))
            : DomainResult.Fail<ScheduleTransitionResult>(commit.Failure!);
    }

    private static string Hash(string? value) => string.IsNullOrWhiteSpace(value) || value.Length > 1024 ||
        value.Any(char.IsControl)
        ? ScheduleStateMachine.EmptyHash
        : $"sha256:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))}";

    private static DomainResult<T> Invalid<T>(string message) =>
        DomainResult.Fail<T>(new DomainFailure(FailureCode.ValidationFailure, message));

    private static DomainResult<T> Conflict<T>(string message) =>
        DomainResult.Fail<T>(new DomainFailure(FailureCode.ConcurrencyConflict, message));
}
