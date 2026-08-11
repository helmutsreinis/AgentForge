using System.Text.Json;
using AgentForge.Abstractions.Orchestration;
using AgentForge.Domain.Orchestration;
using AgentForge.Domain.Primitives;
using AgentForge.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace AgentForge.Persistence;

internal sealed class SqliteTaskSnapshotStore(AgentForgeDbContext dbContext) : ITaskSnapshotStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public async ValueTask AppendAsync(
        OrchestrationTaskSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!OrchestrationTaskStateMachine.IsConsistent(snapshot))
        {
            throw new ArgumentException("Only a self-consistent orchestration snapshot can be persisted.", nameof(snapshot));
        }

        await dbContext.OrchestrationTaskSnapshots.AddAsync(Map(snapshot), cancellationToken);
    }

    public async ValueTask<OrchestrationTaskSnapshot?> FindLatestAsync(
        OrchestrationTaskId taskId,
        CancellationToken cancellationToken)
    {
        var entity = await dbContext.OrchestrationTaskSnapshots.AsNoTracking()
            .Where(item => item.TaskId == taskId.Value)
            .OrderByDescending(item => item.Version)
            .FirstOrDefaultAsync(cancellationToken);
        return entity is null ? null : Map(entity);
    }

    public async ValueTask<OrchestrationTaskSnapshot?> FindByIdempotencyKeyAsync(
        InstallationId installationId,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        var entity = await dbContext.OrchestrationTaskSnapshots.AsNoTracking()
            .Where(item => item.InstallationId == installationId.Value && item.IdempotencyKey == idempotencyKey)
            .OrderByDescending(item => item.Version)
            .FirstOrDefaultAsync(cancellationToken);
        return entity is null ? null : Map(entity);
    }

    public async ValueTask<IReadOnlyList<OrchestrationTaskSnapshot>> ListAsync(
        OrchestrationTaskId taskId,
        CancellationToken cancellationToken)
    {
        var entities = await dbContext.OrchestrationTaskSnapshots.AsNoTracking()
            .Where(item => item.TaskId == taskId.Value)
            .OrderBy(item => item.Version)
            .ToArrayAsync(cancellationToken);
        return entities.Select(Map).ToArray();
    }

    private static OrchestrationTaskSnapshotEntity Map(OrchestrationTaskSnapshot snapshot) => new()
    {
        TaskId = snapshot.Definition.Id.Value,
        Version = snapshot.Version,
        InstallationId = snapshot.Definition.InstallationId.Value,
        AgentId = snapshot.Definition.AgentId.Value,
        State = snapshot.State.ToString(),
        PreviousSnapshotHash = snapshot.PreviousSnapshotHash,
        SnapshotHash = snapshot.SnapshotHash,
        SnapshotJson = JsonSerializer.Serialize(snapshot, SerializerOptions),
        CreatedAtUtcTicks = snapshot.CreatedAt.UtcTicks,
        UpdatedAtUtcTicks = snapshot.UpdatedAt.UtcTicks,
        ActorId = snapshot.ActorId.Value,
        IdempotencyKey = snapshot.IdempotencyKey,
        CorrelationId = snapshot.CorrelationId.Value,
        CausationId = snapshot.CausationId?.Value,
    };

    private static OrchestrationTaskSnapshot Map(OrchestrationTaskSnapshotEntity entity)
    {
        var snapshot = JsonSerializer.Deserialize<OrchestrationTaskSnapshot>(entity.SnapshotJson, SerializerOptions)
            ?? throw new InvalidOperationException("The persisted orchestration snapshot was empty.");
        if (snapshot.Definition.Id.Value != entity.TaskId || snapshot.Version != entity.Version ||
            snapshot.Definition.InstallationId.Value != entity.InstallationId ||
            snapshot.Definition.AgentId.Value != entity.AgentId ||
            !string.Equals(snapshot.State.ToString(), entity.State, StringComparison.Ordinal) ||
            !string.Equals(snapshot.PreviousSnapshotHash, entity.PreviousSnapshotHash, StringComparison.Ordinal) ||
            !string.Equals(snapshot.SnapshotHash, entity.SnapshotHash, StringComparison.Ordinal) ||
            snapshot.CreatedAt.UtcTicks != entity.CreatedAtUtcTicks ||
            snapshot.UpdatedAt.UtcTicks != entity.UpdatedAtUtcTicks ||
            !string.Equals(snapshot.ActorId.Value, entity.ActorId, StringComparison.Ordinal) ||
            !string.Equals(snapshot.IdempotencyKey, entity.IdempotencyKey, StringComparison.Ordinal) ||
            !string.Equals(snapshot.CorrelationId.Value, entity.CorrelationId, StringComparison.Ordinal) ||
            !string.Equals(snapshot.CausationId?.Value, entity.CausationId, StringComparison.Ordinal) ||
            !OrchestrationTaskStateMachine.IsConsistent(snapshot))
        {
            throw new InvalidOperationException("The persisted orchestration snapshot failed integrity validation.");
        }

        return snapshot;
    }
}
