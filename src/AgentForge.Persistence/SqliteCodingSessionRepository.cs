using System.Text.Json;
using AgentForge.Abstractions.Coding;
using AgentForge.Domain.Coding;
using AgentForge.Domain.Primitives;
using AgentForge.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace AgentForge.Persistence;

internal sealed class SqliteCodingSessionRepository(AgentForgeDbContext dbContext) : ICodingSessionRepository
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public async ValueTask AppendAsync(CodingSessionSnapshot snapshot, CancellationToken cancellationToken)
    {
        if (!CodingSessionStateMachine.IsConsistent(snapshot))
        {
            throw new ArgumentException("Only a consistent coding snapshot can be persisted.", nameof(snapshot));
        }

        await dbContext.CodingSessionSnapshots.AddAsync(new CodingSessionSnapshotEntity
        {
            SessionId = snapshot.Id.Value,
            Version = snapshot.Version,
            InstallationId = snapshot.InstallationId.Value,
            AgentId = snapshot.AgentId.Value,
            State = snapshot.State.ToString(),
            PreviousSnapshotHash = snapshot.PreviousSnapshotHash,
            SnapshotHash = snapshot.SnapshotHash,
            SnapshotJson = JsonSerializer.Serialize(snapshot, SerializerOptions),
            UpdatedAtUtcTicks = snapshot.UpdatedAt.UtcTicks,
            ActorId = snapshot.ActorId.Value,
            IdempotencyKey = snapshot.IdempotencyKey,
            CorrelationId = snapshot.CorrelationId.Value,
            CausationId = snapshot.CausationId?.Value,
        }, cancellationToken);
    }

    public async ValueTask<CodingSessionSnapshot?> FindLatestAsync(
        CodingSessionId sessionId,
        CancellationToken cancellationToken) => Map(await dbContext.CodingSessionSnapshots.AsNoTracking()
        .Where(item => item.SessionId == sessionId.Value)
        .OrderByDescending(item => item.Version).FirstOrDefaultAsync(cancellationToken));

    public async ValueTask<CodingSessionSnapshot?> FindByIdempotencyKeyAsync(
        InstallationId installationId,
        string idempotencyKey,
        CancellationToken cancellationToken) => Map(await dbContext.CodingSessionSnapshots.AsNoTracking()
        .Where(item => item.InstallationId == installationId.Value && item.IdempotencyKey == idempotencyKey)
        .OrderByDescending(item => item.Version).FirstOrDefaultAsync(cancellationToken));

    private static CodingSessionSnapshot? Map(CodingSessionSnapshotEntity? entity)
    {
        if (entity is null) return null;
        var snapshot = JsonSerializer.Deserialize<CodingSessionSnapshot>(entity.SnapshotJson, SerializerOptions)
            ?? throw new InvalidOperationException("The persisted coding snapshot was empty.");
        if (snapshot.Id.Value != entity.SessionId || snapshot.Version != entity.Version ||
            snapshot.InstallationId.Value != entity.InstallationId || snapshot.AgentId.Value != entity.AgentId ||
            snapshot.State.ToString() != entity.State || snapshot.PreviousSnapshotHash != entity.PreviousSnapshotHash ||
            snapshot.SnapshotHash != entity.SnapshotHash || snapshot.UpdatedAt.UtcTicks != entity.UpdatedAtUtcTicks ||
            snapshot.ActorId.Value != entity.ActorId || snapshot.IdempotencyKey != entity.IdempotencyKey ||
            snapshot.CorrelationId.Value != entity.CorrelationId || snapshot.CausationId?.Value != entity.CausationId ||
            !CodingSessionStateMachine.IsConsistent(snapshot))
        {
            throw new InvalidOperationException("The persisted coding snapshot failed integrity validation.");
        }

        return snapshot;
    }
}
