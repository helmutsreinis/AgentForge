using System.Text.Json;
using AgentForge.Abstractions.Runtime;
using AgentForge.Domain.Orchestration;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Runtime;
using AgentForge.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace AgentForge.Persistence;

internal sealed class SqliteRunConversationRepository(AgentForgeDbContext dbContext) : IRunConversationRepository
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public async ValueTask AppendAsync(
        RunConversationSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!RunConversationStateMachine.IsConsistent(snapshot))
            throw new ArgumentException("Only a self-consistent run conversation can be persisted.", nameof(snapshot));

        await dbContext.RunConversationSnapshots.AddAsync(Map(snapshot), cancellationToken);
    }

    public async ValueTask<RunConversationSnapshot?> FindLatestAsync(
        RunConversationId conversationId,
        CancellationToken cancellationToken) => Map(await dbContext.RunConversationSnapshots.AsNoTracking()
        .Where(item => item.ConversationId == conversationId.Value)
        .OrderByDescending(item => item.Version)
        .FirstOrDefaultAsync(cancellationToken));

    public async ValueTask<RunConversationSnapshot?> FindByIdempotencyKeyAsync(
        InstallationId installationId,
        string idempotencyKey,
        CancellationToken cancellationToken) => Map(await dbContext.RunConversationSnapshots.AsNoTracking()
        .Where(item => item.InstallationId == installationId.Value && item.IdempotencyKey == idempotencyKey)
        .OrderByDescending(item => item.Version)
        .FirstOrDefaultAsync(cancellationToken));

    public async ValueTask<RunConversationSnapshot?> FindByTaskIdAsync(
        InstallationId installationId,
        OrchestrationTaskId taskId,
        CancellationToken cancellationToken)
    {
        var latest = await ListLatestAsync(installationId, 500, cancellationToken);
        return latest.SingleOrDefault(snapshot => snapshot.Turns.Any(turn => turn.TaskId == taskId));
    }

    public async ValueTask<IReadOnlyList<RunConversationSnapshot>> ListLatestAsync(
        InstallationId installationId,
        int maximumResults,
        CancellationToken cancellationToken)
    {
        if (installationId.Value == Guid.Empty || maximumResults is < 1 or > 500)
            throw new ArgumentOutOfRangeException(nameof(maximumResults));

        var latestVersions = dbContext.RunConversationSnapshots.AsNoTracking()
            .Where(item => item.InstallationId == installationId.Value)
            .GroupBy(item => item.ConversationId)
            .Select(group => new { ConversationId = group.Key, Version = group.Max(item => item.Version) });
        var entities = await dbContext.RunConversationSnapshots.AsNoTracking()
            .Join(
                latestVersions,
                item => new { item.ConversationId, item.Version },
                latest => new { latest.ConversationId, latest.Version },
                (item, _) => item)
            .OrderByDescending(item => item.UpdatedAtUtcTicks)
            .ThenBy(item => item.ConversationId)
            .Take(maximumResults)
            .ToArrayAsync(cancellationToken);
        return entities.Select(Map).ToArray()!;
    }

    private static RunConversationSnapshotEntity Map(RunConversationSnapshot snapshot) => new()
    {
        ConversationId = snapshot.Id.Value,
        Version = snapshot.Version,
        InstallationId = snapshot.InstallationId.Value,
        AgentId = snapshot.AgentId.Value,
        ProviderId = snapshot.ProviderId.Value,
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

    private static RunConversationSnapshot? Map(RunConversationSnapshotEntity? entity)
    {
        if (entity is null) return null;
        var snapshot = JsonSerializer.Deserialize<RunConversationSnapshot>(entity.SnapshotJson, SerializerOptions)
            ?? throw new InvalidOperationException("The persisted run conversation was empty.");
        if (snapshot.Id.Value != entity.ConversationId || snapshot.Version != entity.Version ||
            snapshot.InstallationId.Value != entity.InstallationId || snapshot.AgentId.Value != entity.AgentId ||
            snapshot.ProviderId.Value != entity.ProviderId || snapshot.State.ToString() != entity.State ||
            snapshot.PreviousSnapshotHash != entity.PreviousSnapshotHash || snapshot.SnapshotHash != entity.SnapshotHash ||
            snapshot.CreatedAt.UtcTicks != entity.CreatedAtUtcTicks ||
            snapshot.UpdatedAt.UtcTicks != entity.UpdatedAtUtcTicks || snapshot.ActorId.Value != entity.ActorId ||
            snapshot.IdempotencyKey != entity.IdempotencyKey || snapshot.CorrelationId.Value != entity.CorrelationId ||
            snapshot.CausationId?.Value != entity.CausationId || !RunConversationStateMachine.IsConsistent(snapshot))
        {
            throw new InvalidOperationException("The persisted run conversation failed integrity validation.");
        }
        return snapshot;
    }
}
