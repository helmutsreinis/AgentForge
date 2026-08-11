using System.Text.Json;
using AgentForge.Abstractions.Skills;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Skills;
using AgentForge.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace AgentForge.Persistence;

internal sealed class SqliteSkillRunSnapshotStore(AgentForgeDbContext dbContext) : ISkillRunSnapshotStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public async ValueTask AddAsync(SkillRunSnapshot snapshot, CancellationToken cancellationToken)
    {
        if (!SkillGovernanceStateMachine.IsConsistent(snapshot))
        {
            throw new ArgumentException("Only a consistent skill run snapshot can be persisted.", nameof(snapshot));
        }

        await dbContext.SkillRunSnapshots.AddAsync(new SkillRunSnapshotEntity
        {
            Id = snapshot.Id.Value,
            InstallationId = snapshot.InstallationId.Value,
            IdempotencyKey = snapshot.IdempotencyKey,
            SnapshotHash = snapshot.SnapshotHash,
            SnapshotJson = JsonSerializer.Serialize(snapshot, SerializerOptions),
            CreatedAtUtcTicks = snapshot.CreatedAt.UtcTicks,
            ActorId = snapshot.ActorId.Value,
            CorrelationId = snapshot.CorrelationId.Value,
            CausationId = snapshot.CausationId?.Value,
        }, cancellationToken);
    }

    public async ValueTask<SkillRunSnapshot?> FindAsync(
        SkillRunSnapshotId snapshotId,
        CancellationToken cancellationToken) => Map(await dbContext.SkillRunSnapshots.AsNoTracking()
        .SingleOrDefaultAsync(item => item.Id == snapshotId.Value, cancellationToken));

    public async ValueTask<SkillRunSnapshot?> FindByIdempotencyKeyAsync(
        InstallationId installationId,
        string idempotencyKey,
        CancellationToken cancellationToken) => Map(await dbContext.SkillRunSnapshots.AsNoTracking()
        .SingleOrDefaultAsync(item => item.InstallationId == installationId.Value &&
            item.IdempotencyKey == idempotencyKey, cancellationToken));

    private static SkillRunSnapshot? Map(SkillRunSnapshotEntity? entity)
    {
        if (entity is null)
        {
            return null;
        }

        var snapshot = JsonSerializer.Deserialize<SkillRunSnapshot>(entity.SnapshotJson, SerializerOptions)
            ?? throw new InvalidOperationException("The persisted skill run snapshot was empty.");
        if (snapshot.Id.Value != entity.Id || snapshot.InstallationId.Value != entity.InstallationId ||
            snapshot.IdempotencyKey != entity.IdempotencyKey || snapshot.SnapshotHash != entity.SnapshotHash ||
            snapshot.CreatedAt.UtcTicks != entity.CreatedAtUtcTicks || snapshot.ActorId.Value != entity.ActorId ||
            snapshot.CorrelationId.Value != entity.CorrelationId || snapshot.CausationId?.Value != entity.CausationId ||
            !SkillGovernanceStateMachine.IsConsistent(snapshot))
        {
            throw new InvalidOperationException("The persisted skill run snapshot failed integrity validation.");
        }

        return snapshot;
    }
}
