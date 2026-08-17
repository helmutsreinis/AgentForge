using System.Text.Json;
using AgentForge.Abstractions.HttpApi;
using AgentForge.Domain.HttpApi;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Security;
using AgentForge.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace AgentForge.Persistence;

internal sealed class SqliteHttpApiProfileRepository(AgentForgeDbContext dbContext) : IHttpApiProfileRepository
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async ValueTask<HttpApiProfile?> FindAsync(
        InstallationId installationId,
        HttpApiProfileId profileId,
        CancellationToken cancellationToken)
    {
        var entity = await dbContext.HttpApiProfiles.AsNoTracking().SingleOrDefaultAsync(item =>
            item.InstallationId == installationId.Value && item.ProfileId == profileId.Value, cancellationToken);
        return entity is null ? null : Map(entity);
    }

    public async Task<IReadOnlyList<HttpApiProfile>> ListAsync(
        InstallationId installationId,
        CancellationToken cancellationToken) => (await dbContext.HttpApiProfiles.AsNoTracking()
            .Where(item => item.InstallationId == installationId.Value)
            .OrderBy(item => item.ProfileId)
            .ToArrayAsync(cancellationToken)).Select(Map).ToArray();

    public async ValueTask AddAsync(HttpApiProfile profile, CancellationToken cancellationToken) =>
        await dbContext.HttpApiProfiles.AddAsync(Map(profile), cancellationToken);

    public ValueTask UpdateAsync(
        HttpApiProfile profile,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var entity = Map(profile);
        dbContext.HttpApiProfiles.Attach(entity);
        var entry = dbContext.Entry(entity);
        entry.State = EntityState.Modified;
        entry.Property(item => item.Version).OriginalValue = expectedVersion;
        return ValueTask.CompletedTask;
    }

    private static HttpApiProfileEntity Map(HttpApiProfile profile) => new()
    {
        InstallationId = profile.InstallationId.Value,
        ProfileId = profile.Id.Value,
        DisplayName = profile.DisplayName,
        BaseEndpoint = profile.BaseEndpoint.AbsoluteUri,
        ProbeRelativePath = profile.ProbeRelativePath,
        StaticHeadersJson = JsonSerializer.Serialize(profile.StaticHeaders, Json),
        SecretStore = profile.CredentialReference.Store,
        SecretKey = profile.CredentialReference.Key,
        IsEnabled = profile.IsEnabled,
        Version = profile.Version,
        CreatedAtUtc = profile.CreatedAtUtc,
        UpdatedAtUtc = profile.UpdatedAtUtc,
        ActorId = profile.ActorId.Value,
        CorrelationId = profile.CorrelationId.Value,
    };

    private static HttpApiProfile Map(HttpApiProfileEntity entity) => new(
        new InstallationId(entity.InstallationId),
        new HttpApiProfileId(entity.ProfileId),
        entity.DisplayName,
        new Uri(entity.BaseEndpoint, UriKind.Absolute),
        entity.ProbeRelativePath,
        JsonSerializer.Deserialize<Dictionary<string, string>>(entity.StaticHeadersJson, Json)
            ?? throw new InvalidOperationException("The persisted HTTP API headers were empty."),
        new SecretReference(entity.SecretStore, entity.SecretKey),
        entity.IsEnabled,
        entity.Version,
        entity.CreatedAtUtc,
        entity.UpdatedAtUtc,
        new ActorId(entity.ActorId),
        new CorrelationId(entity.CorrelationId));
}
