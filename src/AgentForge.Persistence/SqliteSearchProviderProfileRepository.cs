using AgentForge.Abstractions.Search;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Search;
using AgentForge.Domain.Security;
using AgentForge.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace AgentForge.Persistence;

internal sealed class SqliteSearchProviderProfileRepository(AgentForgeDbContext dbContext)
    : ISearchProviderProfileRepository
{
    public async ValueTask<SearchProviderProfile?> FindAsync(
        InstallationId installationId,
        string providerId,
        CancellationToken cancellationToken)
    {
        var entity = await dbContext.SearchProviderProfiles.AsNoTracking().SingleOrDefaultAsync(
            item => item.InstallationId == installationId.Value && item.Id == providerId,
            cancellationToken);
        return entity is null ? null : Map(entity);
    }

    public async Task<IReadOnlyList<SearchProviderProfile>> ListAsync(
        InstallationId installationId,
        CancellationToken cancellationToken)
    {
        var entities = await dbContext.SearchProviderProfiles.AsNoTracking()
            .Where(item => item.InstallationId == installationId.Value)
            .OrderBy(item => item.Id)
            .ToListAsync(cancellationToken);
        return entities.Select(Map).ToArray();
    }

    public async ValueTask AddAsync(SearchProviderProfile profile, CancellationToken cancellationToken) =>
        await dbContext.SearchProviderProfiles.AddAsync(Map(profile), cancellationToken);

    public ValueTask UpdateAsync(
        SearchProviderProfile profile,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var entity = Map(profile);
        dbContext.SearchProviderProfiles.Attach(entity);
        var entry = dbContext.Entry(entity);
        entry.State = EntityState.Modified;
        entry.Property(item => item.Version).OriginalValue = expectedVersion;
        return ValueTask.CompletedTask;
    }

    private static SearchProviderProfileEntity Map(SearchProviderProfile profile) => new()
    {
        InstallationId = profile.InstallationId.Value,
        Id = profile.Id,
        Kind = profile.Kind.ToString(),
        Endpoint = profile.Endpoint.AbsoluteUri,
        SecretStore = profile.CredentialReference.Store,
        SecretKey = profile.CredentialReference.Key,
        IsEnabled = profile.IsEnabled,
        SafeSearch = profile.SafeSearch.ToString(),
        CountryCode = profile.CountryCode,
        SearchLanguage = profile.SearchLanguage,
        Version = profile.Version,
        CreatedAtUtc = profile.CreatedAtUtc,
        UpdatedAtUtc = profile.UpdatedAtUtc,
        ActorId = profile.ActorId.Value,
        CorrelationId = profile.CorrelationId.Value,
    };

    private static SearchProviderProfile Map(SearchProviderProfileEntity entity) => new(
        new InstallationId(entity.InstallationId),
        entity.Id,
        Enum.Parse<SearchProviderKind>(entity.Kind, false),
        new Uri(entity.Endpoint, UriKind.Absolute),
        new SecretReference(entity.SecretStore, entity.SecretKey),
        entity.IsEnabled,
        Enum.Parse<SearchSafeSearch>(entity.SafeSearch, false),
        entity.CountryCode,
        entity.SearchLanguage,
        entity.Version,
        entity.CreatedAtUtc,
        entity.UpdatedAtUtc,
        new ActorId(entity.ActorId),
        new CorrelationId(entity.CorrelationId));
}
