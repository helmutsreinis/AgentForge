using AgentForge.Abstractions.Models;
using AgentForge.Domain.Installations;
using AgentForge.Domain.Models;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Providers;
using AgentForge.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace AgentForge.Persistence;

internal sealed class SqliteModelProviderHealthRepository(AgentForgeDbContext dbContext)
    : IModelProviderHealthRepository, IModelProviderHealthSource
{
    public async ValueTask<ModelProviderHealthRecord?> FindAsync(
        ProviderProfileId profileId,
        CancellationToken cancellationToken)
    {
        var entity = await dbContext.ModelProviderHealth.AsNoTracking()
            .SingleOrDefaultAsync(item => item.ProfileId == profileId.Value, cancellationToken);
        return entity is null ? null : Map(entity);
    }

    public async ValueTask<DomainResult<IReadOnlyList<ModelProviderHealthEvidence>>> ReadAsync(
        CancellationToken cancellationToken)
    {
        var records = await dbContext.ModelProviderHealth.AsNoTracking()
            .OrderBy(item => item.ProfileId)
            .Select(item => new ModelProviderHealthEvidence(
                new ProviderProfileId(item.ProfileId),
                Enum.Parse<ModelProviderHealthStatus>(item.Status, ignoreCase: false),
                Enum.Parse<ModelHealthEvidenceSource>(item.Source, ignoreCase: false),
                item.ConsecutiveFailures,
                item.EvidenceCode,
                Timestamp(item.ObservedAtUtcTicks),
                Timestamp(item.ExpiresAtUtcTicks),
                Timestamp(item.RetryAfterUtcTicks)))
            .ToArrayAsync(cancellationToken);
        return DomainResult.Success<IReadOnlyList<ModelProviderHealthEvidence>>(records);
    }

    public async ValueTask AddAsync(
        ModelProviderHealthRecord record,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        await dbContext.ModelProviderHealth.AddAsync(Map(record), cancellationToken);
    }

    public ValueTask UpdateAsync(
        ModelProviderHealthRecord record,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        cancellationToken.ThrowIfCancellationRequested();
        var entity = Map(record);
        var tracked = dbContext.ModelProviderHealth.Local.SingleOrDefault(item => item.ProfileId == entity.ProfileId);
        if (tracked is not null)
        {
            var trackedEntry = dbContext.Entry(tracked);
            trackedEntry.CurrentValues.SetValues(entity);
            trackedEntry.Property(item => item.Version).OriginalValue = expectedVersion;
        }
        else
        {
            var entry = dbContext.Attach(entity);
            entry.State = EntityState.Modified;
            entry.Property(item => item.Version).OriginalValue = expectedVersion;
        }

        return ValueTask.CompletedTask;
    }

    private static ModelProviderHealthEntity Map(ModelProviderHealthRecord record) => new()
    {
        ProfileId = record.Evidence.ProfileId.Value,
        InstallationId = record.InstallationId.Value,
        Status = record.Evidence.Status.ToString(),
        Source = record.Evidence.Source.ToString(),
        ConsecutiveFailures = record.Evidence.ConsecutiveFailures,
        EvidenceCode = record.Evidence.EvidenceCode,
        ObservedAtUtcTicks = record.Evidence.ObservedAt.UtcTicks,
        ExpiresAtUtcTicks = record.Evidence.ExpiresAt.UtcTicks,
        RetryAfterUtcTicks = record.Evidence.RetryAfter?.UtcTicks,
        LastRunId = record.LastRunId.Value,
        LastAttemptId = record.LastAttemptId.Value,
        ActorId = record.ActorId.Value,
        CorrelationId = record.CorrelationId.Value,
        CausationId = record.CausationId?.Value,
        UpdatedAtUtcTicks = record.UpdatedAt.UtcTicks,
        Version = record.Version,
    };

    private static ModelProviderHealthRecord Map(ModelProviderHealthEntity entity) => new(
        new InstallationId(entity.InstallationId),
        new ModelProviderHealthEvidence(
            new ProviderProfileId(entity.ProfileId),
            Enum.Parse<ModelProviderHealthStatus>(entity.Status, ignoreCase: false),
            Enum.Parse<ModelHealthEvidenceSource>(entity.Source, ignoreCase: false),
            entity.ConsecutiveFailures,
            entity.EvidenceCode,
            Timestamp(entity.ObservedAtUtcTicks),
            Timestamp(entity.ExpiresAtUtcTicks),
            Timestamp(entity.RetryAfterUtcTicks)),
        new ModelRunId(entity.LastRunId),
        new ModelRunAttemptId(entity.LastAttemptId),
        new ActorId(entity.ActorId),
        new CorrelationId(entity.CorrelationId),
        entity.CausationId is null ? null : new CorrelationId(entity.CausationId),
        Timestamp(entity.UpdatedAtUtcTicks),
        entity.Version);

    private static DateTimeOffset Timestamp(long value) => new(value, TimeSpan.Zero);

    private static DateTimeOffset? Timestamp(long? value) =>
        value is null ? null : new DateTimeOffset(value.Value, TimeSpan.Zero);
}
