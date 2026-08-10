using AgentForge.Abstractions.Providers;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Providers;
using AgentForge.Domain.Security;
using AgentForge.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace AgentForge.Persistence;

internal sealed class SqliteProviderProfileRepository(AgentForgeDbContext dbContext) : IProviderProfileRepository
{
    public async ValueTask AddAsync(ProviderProfile profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        await dbContext.ProviderProfiles.AddAsync(Map(profile), cancellationToken);
    }

    public ValueTask UpdateAsync(
        ProviderProfile profile,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        cancellationToken.ThrowIfCancellationRequested();
        var entity = dbContext.ChangeTracker
            .Entries<ProviderProfileEntity>()
            .FirstOrDefault(item => item.Entity.Id == profile.Id.Value)
            ?.Entity;
        if (entity is null)
        {
            entity = Map(profile);
            dbContext.ProviderProfiles.Attach(entity);
        }
        else
        {
            dbContext.Entry(entity).CurrentValues.SetValues(Map(profile));
        }

        var entry = dbContext.Entry(entity);
        entry.State = EntityState.Modified;
        entry.Property(item => item.Version).OriginalValue = expectedVersion;
        return ValueTask.CompletedTask;
    }

    public async ValueTask<ProviderProfile?> FindByIdAsync(
        ProviderProfileId profileId,
        CancellationToken cancellationToken)
    {
        var entity = await dbContext.ProviderProfiles
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == profileId.Value, cancellationToken);
        return entity is null ? null : Map(entity);
    }

    public async ValueTask<ProviderProfile?> FindByNameAsync(
        InstallationId installationId,
        string name,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var entity = await dbContext.ProviderProfiles
            .AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.InstallationId == installationId.Value && item.Name == name,
                cancellationToken);
        return entity is null ? null : Map(entity);
    }

    public async Task<IReadOnlyList<ProviderProfile>> ListAsync(
        InstallationId installationId,
        CancellationToken cancellationToken)
    {
        var entities = await dbContext.ProviderProfiles
            .AsNoTracking()
            .Where(item => item.InstallationId == installationId.Value)
            .OrderBy(item => item.Name)
            .ToListAsync(cancellationToken);
        return entities.Select(Map).ToArray();
    }

    private static ProviderProfileEntity Map(ProviderProfile profile) => new()
    {
        Id = profile.Id.Value,
        InstallationId = profile.InstallationId.Value,
        Name = profile.Name,
        ProviderType = profile.ProviderType,
        Endpoint = profile.Endpoint.AbsoluteUri,
        Model = profile.Model,
        SecretStore = profile.SecretReference.Store,
        SecretKey = profile.SecretReference.Key,
        TextGeneration = profile.Capabilities.TextGeneration,
        Streaming = profile.Capabilities.Streaming,
        ToolCalls = profile.Capabilities.ToolCalls,
        Images = profile.Capabilities.Images,
        EvidenceSource = profile.Capabilities.EvidenceSource,
        Version = profile.Version,
        CreatedAt = profile.CreatedAt,
        UpdatedAt = profile.UpdatedAt,
        ActorId = profile.ActorId.Value,
        CorrelationId = profile.CorrelationId.Value,
    };

    private static ProviderProfile Map(ProviderProfileEntity entity) => new(
        new ProviderProfileId(entity.Id),
        new InstallationId(entity.InstallationId),
        entity.Name,
        entity.ProviderType,
        new Uri(entity.Endpoint, UriKind.Absolute),
        entity.Model,
        new SecretReference(entity.SecretStore, entity.SecretKey),
        new ProviderCapabilitySummary(
            entity.TextGeneration,
            entity.Streaming,
            entity.ToolCalls,
            entity.Images,
            entity.EvidenceSource),
        entity.Version,
        entity.CreatedAt,
        entity.UpdatedAt,
        new ActorId(entity.ActorId),
        new CorrelationId(entity.CorrelationId));
}
