using AgentForge.Abstractions.Setup;
using AgentForge.Domain.Artifacts;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Setup;
using AgentForge.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace AgentForge.Persistence;

internal sealed class SqliteSetupProfileSnapshotRepository(AgentForgeDbContext dbContext)
    : ISetupProfileSnapshotRepository
{
    public async ValueTask AddAsync(SetupProfileSnapshot snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        await dbContext.SetupProfileSnapshots.AddAsync(Map(snapshot), cancellationToken);
    }

    public async ValueTask<SetupProfileSnapshot?> FindByIdAsync(
        SetupProfileSnapshotId snapshotId,
        CancellationToken cancellationToken)
    {
        var entity = await dbContext.SetupProfileSnapshots
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == snapshotId.Value, cancellationToken);
        return entity is null ? null : Map(entity);
    }

    public async Task<IReadOnlyList<SetupProfileSnapshot>> ListAsync(
        InstallationId installationId,
        CancellationToken cancellationToken)
    {
        var entities = await dbContext.SetupProfileSnapshots
            .AsNoTracking()
            .Where(item => item.InstallationId == installationId.Value)
            .ToListAsync(cancellationToken);
        return entities
            .OrderByDescending(item => item.CreatedAt)
            .ThenBy(item => item.Kind, StringComparer.Ordinal)
            .Select(Map)
            .ToArray();
    }

    private static SetupProfileSnapshotEntity Map(SetupProfileSnapshot snapshot) => new()
    {
        Id = snapshot.Id.Value,
        InstallationId = snapshot.InstallationId.Value,
        ProfileVersion = snapshot.ProfileVersion,
        Kind = snapshot.Kind.ToString(),
        ArtifactContentHash = snapshot.Artifact.ContentHash,
        ArtifactLength = snapshot.Artifact.Length,
        ArtifactMediaType = snapshot.Artifact.MediaType,
        ArtifactCreatedAt = snapshot.Artifact.CreatedAt,
        CreatedAt = snapshot.CreatedAt,
        ActorId = snapshot.ActorId.Value,
        CorrelationId = snapshot.CorrelationId.Value,
    };

    private static SetupProfileSnapshot Map(SetupProfileSnapshotEntity entity) => new(
        new SetupProfileSnapshotId(entity.Id),
        new InstallationId(entity.InstallationId),
        entity.ProfileVersion,
        Enum.Parse<SetupProfileSnapshotKind>(entity.Kind, ignoreCase: false),
        new ArtifactReference(
            entity.ArtifactContentHash,
            entity.ArtifactLength,
            entity.ArtifactMediaType,
            entity.ArtifactCreatedAt),
        entity.CreatedAt,
        new ActorId(entity.ActorId),
        new CorrelationId(entity.CorrelationId));
}
