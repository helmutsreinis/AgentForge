using System.Text.Json;
using AgentForge.Abstractions.Scheduling;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Scheduling;
using AgentForge.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace AgentForge.Persistence;

internal sealed class SqliteScheduleSnapshotStore(AgentForgeDbContext dbContext) : IScheduleSnapshotStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public async ValueTask AppendAsync(ScheduleSnapshot snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!ScheduleStateMachine.IsConsistent(snapshot))
        {
            throw new ArgumentException("Only a self-consistent schedule snapshot can be persisted.", nameof(snapshot));
        }

        await dbContext.ScheduleSnapshots.AddAsync(new ScheduleSnapshotEntity
        {
            ScheduleId = snapshot.Definition.Id.Value,
            Version = snapshot.Version,
            InstallationId = snapshot.Definition.InstallationId.Value,
            AgentId = snapshot.Definition.AgentId.Value,
            State = snapshot.State.ToString(),
            NextScheduledAtUtcTicks = snapshot.NextScheduledFor?.UtcTicks,
            NextDueAtUtcTicks = snapshot.NextDueAt?.UtcTicks,
            PreviousSnapshotHash = snapshot.PreviousSnapshotHash,
            SnapshotHash = snapshot.SnapshotHash,
            SnapshotJson = JsonSerializer.Serialize(snapshot, SerializerOptions),
            CreatedAtUtcTicks = snapshot.CreatedAt.UtcTicks,
            UpdatedAtUtcTicks = snapshot.UpdatedAt.UtcTicks,
            ActorId = snapshot.ActorId.Value,
            IdempotencyKey = snapshot.IdempotencyKey,
            CorrelationId = snapshot.CorrelationId.Value,
            CausationId = snapshot.CausationId?.Value,
        }, cancellationToken);
    }

    public async ValueTask<ScheduleSnapshot?> FindLatestAsync(
        ScheduleId scheduleId,
        CancellationToken cancellationToken)
    {
        var entity = await dbContext.ScheduleSnapshots.AsNoTracking()
            .Where(item => item.ScheduleId == scheduleId.Value)
            .OrderByDescending(item => item.Version)
            .FirstOrDefaultAsync(cancellationToken);
        return entity is null ? null : Map(entity);
    }

    public async ValueTask<ScheduleSnapshot?> FindByIdempotencyKeyAsync(
        InstallationId installationId,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        var entity = await dbContext.ScheduleSnapshots.AsNoTracking()
            .Where(item => item.InstallationId == installationId.Value && item.IdempotencyKey == idempotencyKey)
            .OrderByDescending(item => item.Version)
            .FirstOrDefaultAsync(cancellationToken);
        return entity is null ? null : Map(entity);
    }

    public async ValueTask<IReadOnlyList<ScheduleSnapshot>> ListAsync(
        ScheduleId scheduleId,
        CancellationToken cancellationToken)
    {
        var entities = await dbContext.ScheduleSnapshots.AsNoTracking()
            .Where(item => item.ScheduleId == scheduleId.Value)
            .OrderBy(item => item.Version)
            .ToArrayAsync(cancellationToken);
        return entities.Select(Map).ToArray();
    }

    public async ValueTask<IReadOnlyList<DueSchedule>> ListDueAsync(
        DateTimeOffset now,
        int maximumCount,
        CancellationToken cancellationToken)
    {
        if (maximumCount is < 1 or > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        }

        var latestVersions = dbContext.ScheduleSnapshots
            .GroupBy(item => item.ScheduleId)
            .Select(group => new { ScheduleId = group.Key, Version = group.Max(item => item.Version) });
        var due = await dbContext.ScheduleSnapshots.AsNoTracking()
            .Join(
                latestVersions,
                snapshot => new { snapshot.ScheduleId, snapshot.Version },
                latest => new { latest.ScheduleId, latest.Version },
                (snapshot, _) => snapshot)
            .Where(item => item.State == nameof(ScheduleState.Active) &&
                item.NextDueAtUtcTicks != null && item.NextDueAtUtcTicks <= now.UtcTicks)
            .OrderBy(item => item.NextDueAtUtcTicks)
            .ThenBy(item => item.ScheduleId)
            .Take(maximumCount)
            .Select(item => new DueSchedule(
                new ScheduleId(item.ScheduleId),
                item.Version,
                new DateTimeOffset(item.NextDueAtUtcTicks!.Value, TimeSpan.Zero)))
            .ToArrayAsync(cancellationToken);
        return due;
    }

    public async ValueTask<IReadOnlyList<ScheduleSnapshot>> ListLatestAsync(
        InstallationId installationId,
        int maximumCount,
        CancellationToken cancellationToken)
    {
        if (installationId.Value == Guid.Empty || maximumCount is < 1 or > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        }

        var latestVersions = dbContext.ScheduleSnapshots
            .Where(item => item.InstallationId == installationId.Value)
            .GroupBy(item => item.ScheduleId)
            .Select(group => new { ScheduleId = group.Key, Version = group.Max(item => item.Version) });
        var entities = await dbContext.ScheduleSnapshots.AsNoTracking()
            .Join(
                latestVersions,
                snapshot => new { snapshot.ScheduleId, snapshot.Version },
                latest => new { latest.ScheduleId, latest.Version },
                (snapshot, _) => snapshot)
            .OrderByDescending(item => item.UpdatedAtUtcTicks)
            .ThenBy(item => item.ScheduleId)
            .Take(maximumCount)
            .ToArrayAsync(cancellationToken);
        return entities.Select(Map).ToArray();
    }

    public async ValueTask<IReadOnlyList<RunnableScheduleOccurrence>> ListRunnableAsync(
        DateTimeOffset now,
        int maximumCount,
        CancellationToken cancellationToken)
    {
        if (maximumCount is < 1 or > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        }

        var latestVersions = dbContext.ScheduleSnapshots
            .GroupBy(item => item.ScheduleId)
            .Select(group => new { ScheduleId = group.Key, Version = group.Max(item => item.Version) });
        var entities = await dbContext.ScheduleSnapshots.AsNoTracking()
            .Join(
                latestVersions,
                snapshot => new { snapshot.ScheduleId, snapshot.Version },
                latest => new { latest.ScheduleId, latest.Version },
                (snapshot, _) => snapshot)
            .Where(item => item.State == nameof(ScheduleState.Active))
            .OrderBy(item => item.UpdatedAtUtcTicks)
            .Take(512)
            .ToArrayAsync(cancellationToken);
        return entities.Select(Map)
            .SelectMany(snapshot => snapshot.Occurrences
                .Where(occurrence =>
                    occurrence.State is ScheduleOccurrenceState.Queued && occurrence.DueAt <= now &&
                        (occurrence.RetryNotBefore is null || occurrence.RetryNotBefore <= now) ||
                    occurrence.State is ScheduleOccurrenceState.Running &&
                        occurrence.Lease is { ExpiresAt: var expiresAt } && expiresAt <= now)
                .Select(occurrence => new RunnableScheduleOccurrence(
                    snapshot.Definition.Id,
                    snapshot.Version,
                    occurrence.IdempotencyKeyHash,
                    occurrence.DueAt,
                    occurrence.State is ScheduleOccurrenceState.Running)))
            .OrderBy(item => item.DueAt)
            .ThenBy(item => item.ScheduleId.Value)
            .Take(maximumCount)
            .ToArray();
    }

    private static ScheduleSnapshot Map(ScheduleSnapshotEntity entity)
    {
        var snapshot = JsonSerializer.Deserialize<ScheduleSnapshot>(entity.SnapshotJson, SerializerOptions)
            ?? throw new InvalidOperationException("The persisted schedule snapshot was empty.");
        if (snapshot.Definition.Id.Value != entity.ScheduleId || snapshot.Version != entity.Version ||
            snapshot.Definition.InstallationId.Value != entity.InstallationId ||
            snapshot.Definition.AgentId.Value != entity.AgentId ||
            !string.Equals(snapshot.State.ToString(), entity.State, StringComparison.Ordinal) ||
            snapshot.NextScheduledFor?.UtcTicks != entity.NextScheduledAtUtcTicks ||
            snapshot.NextDueAt?.UtcTicks != entity.NextDueAtUtcTicks ||
            !string.Equals(snapshot.PreviousSnapshotHash, entity.PreviousSnapshotHash, StringComparison.Ordinal) ||
            !string.Equals(snapshot.SnapshotHash, entity.SnapshotHash, StringComparison.Ordinal) ||
            snapshot.CreatedAt.UtcTicks != entity.CreatedAtUtcTicks ||
            snapshot.UpdatedAt.UtcTicks != entity.UpdatedAtUtcTicks ||
            !string.Equals(snapshot.ActorId.Value, entity.ActorId, StringComparison.Ordinal) ||
            !string.Equals(snapshot.IdempotencyKey, entity.IdempotencyKey, StringComparison.Ordinal) ||
            !string.Equals(snapshot.CorrelationId.Value, entity.CorrelationId, StringComparison.Ordinal) ||
            !string.Equals(snapshot.CausationId?.Value, entity.CausationId, StringComparison.Ordinal) ||
            !ScheduleStateMachine.IsConsistent(snapshot))
        {
            throw new InvalidOperationException("The persisted schedule snapshot failed integrity validation.");
        }

        return snapshot;
    }
}
