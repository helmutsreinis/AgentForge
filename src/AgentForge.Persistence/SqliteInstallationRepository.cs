using AgentForge.Abstractions.Installations;
using AgentForge.Domain.Installations;
using AgentForge.Domain.Primitives;
using AgentForge.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace AgentForge.Persistence;

internal sealed class SqliteInstallationRepository(AgentForgeDbContext dbContext) : IInstallationRepository
{
    private static readonly ActorId BootstrapActor = new("bootstrap-kernel");
    private static readonly CorrelationId BootstrapCorrelation = new("bootstrap");

    public async ValueTask<InstallationSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        var entity = await dbContext.Installations
            .AsNoTracking()
            .SingleOrDefaultAsync(cancellationToken);

        return entity is null
            ? InstallationSnapshot.CreateUninitialized(
                new InstallationId(Guid.Empty),
                DateTimeOffset.UnixEpoch,
                BootstrapActor,
                BootstrapCorrelation)
            : Map(entity);
    }

    public async ValueTask AddAsync(InstallationSnapshot snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Id.Value == Guid.Empty)
        {
            throw new ArgumentException("A durable installation must have a non-empty identifier.", nameof(snapshot));
        }

        await dbContext.Installations.AddAsync(Map(snapshot), cancellationToken);
    }

    public ValueTask UpdateAsync(
        InstallationSnapshot snapshot,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();

        var entity = Map(snapshot);
        dbContext.Installations.Attach(entity);
        var entry = dbContext.Entry(entity);
        entry.State = EntityState.Modified;
        entry.Property(item => item.Version).OriginalValue = expectedVersion;
        return ValueTask.CompletedTask;
    }

    private static InstallationSnapshot Map(InstallationEntity entity) => new(
        new InstallationId(entity.Id),
        Enum.Parse<InstallationState>(entity.State, ignoreCase: false),
        entity.Version,
        entity.UpdatedAt,
        new ActorId(entity.ActorId),
        new CorrelationId(entity.CorrelationId),
        entity.RecoveryReason);

    private static InstallationEntity Map(InstallationSnapshot snapshot) => new()
    {
        Id = snapshot.Id.Value,
        State = snapshot.State.ToString(),
        Version = snapshot.Version,
        UpdatedAt = snapshot.UpdatedAt,
        ActorId = snapshot.ActorId.Value,
        CorrelationId = snapshot.CorrelationId.Value,
        RecoveryReason = snapshot.RecoveryReason,
    };
}
