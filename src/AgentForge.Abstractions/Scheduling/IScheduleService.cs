using AgentForge.Domain.Primitives;
using AgentForge.Domain.Scheduling;

namespace AgentForge.Abstractions.Scheduling;

public interface ITimeZoneResolver
{
    DomainResult<TimeZoneInfo> Resolve(string timeZoneId);
}

public interface IScheduleSnapshotStore
{
    ValueTask AppendAsync(ScheduleSnapshot snapshot, CancellationToken cancellationToken);

    ValueTask<ScheduleSnapshot?> FindLatestAsync(ScheduleId scheduleId, CancellationToken cancellationToken);

    ValueTask<ScheduleSnapshot?> FindByIdempotencyKeyAsync(
        InstallationId installationId,
        string idempotencyKey,
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<ScheduleSnapshot>> ListAsync(
        ScheduleId scheduleId,
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<DueSchedule>> ListDueAsync(
        DateTimeOffset now,
        int maximumCount,
        CancellationToken cancellationToken);
}

public sealed record DueSchedule(ScheduleId ScheduleId, long Version, DateTimeOffset DueAt);

public sealed record ScheduleTransitionResult(ScheduleSnapshot Snapshot, bool WasReplay = false);

public sealed record ScheduleLeaseGrant(
    ScheduleSnapshot Snapshot,
    string OccurrenceIdHash,
    string LeaseToken,
    DateTimeOffset ExpiresAt);

public interface IScheduleService
{
    Task<DomainResult<ScheduleTransitionResult>> CreateAsync(
        ScheduleDefinition definition,
        ActorId actorId,
        string idempotencyKey,
        CorrelationId correlationId,
        CorrelationId? causationId,
        CancellationToken cancellationToken);

    DomainResult<IReadOnlyList<DateTimeOffset>> Preview(
        ScheduleDefinition definition,
        DateTimeOffset after,
        int count);

    Task<DomainResult<ScheduleTransitionResult>> EvaluateDueAsync(
        ScheduleId scheduleId,
        long expectedVersion,
        CancellationToken cancellationToken);

    Task<DomainResult<ScheduleTransitionResult>> RunNowAsync(
        ScheduleId scheduleId,
        long expectedVersion,
        string idempotencyKey,
        CancellationToken cancellationToken);

    Task<DomainResult<ScheduleLeaseGrant>> ClaimAsync(
        ScheduleId scheduleId,
        long expectedVersion,
        string occurrenceIdHash,
        string owner,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken);

    Task<DomainResult<ScheduleTransitionResult>> CompleteAsync(
        ScheduleId scheduleId,
        long expectedVersion,
        string occurrenceIdHash,
        string owner,
        string leaseToken,
        string evidenceHash,
        CancellationToken cancellationToken);

    Task<DomainResult<ScheduleTransitionResult>> FailAsync(
        ScheduleId scheduleId,
        long expectedVersion,
        string occurrenceIdHash,
        string owner,
        string leaseToken,
        string evidenceHash,
        CancellationToken cancellationToken);

    Task<DomainResult<ScheduleTransitionResult>> RecoverExpiredAsync(
        ScheduleId scheduleId,
        long expectedVersion,
        CancellationToken cancellationToken);

    Task<DomainResult<ScheduleTransitionResult>> PauseAsync(
        ScheduleId scheduleId,
        long expectedVersion,
        CancellationToken cancellationToken);

    Task<DomainResult<ScheduleTransitionResult>> ResumeAsync(
        ScheduleId scheduleId,
        long expectedVersion,
        CancellationToken cancellationToken);
}
