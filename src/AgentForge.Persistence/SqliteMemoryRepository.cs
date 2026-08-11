using AgentForge.Abstractions.Memory;
using AgentForge.Domain.Agents;
using AgentForge.Domain.Memory;
using AgentForge.Domain.Primitives;
using AgentForge.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace AgentForge.Persistence;

internal sealed class SqliteMemoryRepository(AgentForgeDbContext dbContext) : IMemoryRepository
{
    public async ValueTask<MemoryEntry?> FindByIdAsync(MemoryEntryId id, CancellationToken cancellationToken) =>
        MapEntity(await dbContext.MemoryEntries.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id.Value, cancellationToken));

    public async ValueTask<MemoryEntry?> FindByIdempotencyKeyAsync(
        InstallationId installationId,
        string idempotencyKey,
        CancellationToken cancellationToken) => MapEntity(await dbContext.MemoryEntries.AsNoTracking()
            .SingleOrDefaultAsync(item => item.InstallationId == installationId.Value && item.IdempotencyKey == idempotencyKey, cancellationToken));

    public async ValueTask AddAsync(MemoryEntry entry, CancellationToken cancellationToken)
    {
        await dbContext.MemoryEntries.AddAsync(MapRecord(entry), cancellationToken);
    }

    public async ValueTask<IReadOnlyList<MemoryEntry>> SearchAsync(
        MemoryQuery query,
        CancellationToken cancellationToken)
    {
        var kinds = query.Kinds.Select(item => item.ToString()).ToArray();
        var escaped = query.Text.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
        var pattern = $"%{escaped}%";
        var rows = await dbContext.MemoryEntries.AsNoTracking()
            .Where(item => item.InstallationId == query.InstallationId.Value &&
                item.AgentId == query.AgentId.Value && item.ScopeId == query.ScopeId &&
                item.ExpiresAtUtcTicks > query.AsOfUtc.UtcTicks && kinds.Contains(item.Kind) &&
                EF.Functions.Like(item.Content, pattern, "\\"))
            .OrderByDescending(item => item.CreatedAtUtcTicks)
            .ThenBy(item => item.Id)
            .Take(query.MaximumResults)
            .ToArrayAsync(cancellationToken);
        return rows.Select(item => MapEntity(item)!).ToArray();
    }

    public async ValueTask DeleteAsync(MemoryEntryId id, CancellationToken cancellationToken)
    {
        var entity = await dbContext.MemoryEntries.SingleAsync(item => item.Id == id.Value, cancellationToken);
        dbContext.MemoryEntries.Remove(entity);
    }

    private static MemoryEntryEntity MapRecord(MemoryEntry entry) => new()
    {
        Id = entry.Id.Value,
        InstallationId = entry.InstallationId.Value,
        AgentId = entry.AgentId.Value,
        ScopeId = entry.ScopeId,
        Kind = entry.Kind.ToString(),
        Content = entry.Content,
        ContentHash = entry.ContentHash,
        SourceKind = entry.Source.Kind.ToString(),
        SourceId = entry.Source.SourceId,
        SourceEvidenceHash = entry.Source.EvidenceHash,
        SourceUri = entry.Source.SourceUri?.AbsoluteUri,
        CreatedAtUtcTicks = entry.CreatedAtUtc.UtcTicks,
        ExpiresAtUtcTicks = entry.ExpiresAtUtc.UtcTicks,
        Version = entry.Version,
        ActorId = entry.ActorId.Value,
        CorrelationId = entry.CorrelationId.Value,
        CausationId = entry.CausationId?.Value,
        IdempotencyKey = entry.IdempotencyKey,
        RedactionCount = entry.RedactionCount,
    };

    private static MemoryEntry? MapEntity(MemoryEntryEntity? entity) => entity is null ? null : new(
        new MemoryEntryId(entity.Id),
        new InstallationId(entity.InstallationId),
        new AgentIdentityId(entity.AgentId),
        entity.ScopeId,
        Enum.Parse<MemoryKind>(entity.Kind),
        entity.Content,
        entity.ContentHash,
        new MemorySource(
            Enum.Parse<MemorySourceKind>(entity.SourceKind),
            entity.SourceId,
            entity.SourceEvidenceHash,
            entity.SourceUri is null ? null : new Uri(entity.SourceUri)),
        new DateTimeOffset(entity.CreatedAtUtcTicks, TimeSpan.Zero),
        new DateTimeOffset(entity.ExpiresAtUtcTicks, TimeSpan.Zero),
        entity.Version,
        new ActorId(entity.ActorId),
        new CorrelationId(entity.CorrelationId),
        entity.CausationId is null ? null : new CorrelationId(entity.CausationId),
        entity.IdempotencyKey,
        entity.RedactionCount);
}
