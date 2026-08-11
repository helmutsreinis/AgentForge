using System.Collections.ObjectModel;
using System.Text.Json;
using AgentForge.Abstractions.Models;
using AgentForge.Domain.Agents;
using AgentForge.Domain.Models;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Providers;
using AgentForge.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace AgentForge.Persistence;

internal sealed class SqliteModelRunRepository(AgentForgeDbContext dbContext) : IModelRunRepository
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public async ValueTask AddAsync(ModelRunAggregate aggregate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        await dbContext.ModelRuns.AddAsync(Map(aggregate.Run), cancellationToken);
        await dbContext.ModelRunAttempts.AddAsync(Map(aggregate.Attempt), cancellationToken);
    }

    public async ValueTask UpdateAsync(
        ModelRunAggregate aggregate,
        long expectedRunVersion,
        long expectedAttemptVersion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        UpdateEntity(Map(aggregate.Run), expectedRunVersion);
        UpdateEntity(Map(aggregate.Attempt), expectedAttemptVersion);
        await ValueTask.CompletedTask;
    }

    public async ValueTask<ModelRunAggregate?> FindByIdAsync(
        ModelRunId runId,
        CancellationToken cancellationToken)
    {
        var run = await dbContext.ModelRuns.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == runId.Value, cancellationToken);
        return run is null ? null : await ReadAggregateAsync(run, cancellationToken);
    }

    public async ValueTask<ModelRunAggregate?> FindByIdempotencyKeyAsync(
        InstallationId installationId,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        var run = await dbContext.ModelRuns.AsNoTracking()
            .SingleOrDefaultAsync(item =>
                item.InstallationId == installationId.Value && item.IdempotencyKey == idempotencyKey,
                cancellationToken);
        return run is null ? null : await ReadAggregateAsync(run, cancellationToken);
    }

    private async ValueTask<ModelRunAggregate> ReadAggregateAsync(
        ModelRunEntity run,
        CancellationToken cancellationToken)
    {
        var attempt = await dbContext.ModelRunAttempts.AsNoTracking()
            .SingleAsync(item => item.RunId == run.Id && item.Sequence == 1, cancellationToken);
        return new ModelRunAggregate(Map(run), Map(attempt));
    }

    private void UpdateEntity<TEntity>(TEntity entity, long expectedVersion)
        where TEntity : class
    {
        var suppliedEntry = dbContext.Entry(entity);
        var suppliedId = suppliedEntry.Property("Id").CurrentValue;
        var tracked = dbContext.Set<TEntity>().Local.SingleOrDefault(item =>
            Equals(dbContext.Entry(item).Property("Id").CurrentValue, suppliedId));
        if (tracked is not null)
        {
            var trackedEntry = dbContext.Entry(tracked);
            trackedEntry.CurrentValues.SetValues(entity);
            trackedEntry.Property("Version").OriginalValue = expectedVersion;
            return;
        }

        var entry = dbContext.Attach(entity);
        entry.State = EntityState.Modified;
        entry.Property("Version").OriginalValue = expectedVersion;
    }

    private static ModelRunEntity Map(ModelRunRecord run) => new()
    {
        Id = run.Id.Value,
        InstallationId = run.InstallationId.Value,
        InstallationVersion = run.InstallationVersion,
        AgentId = run.AgentId.Value,
        AgentVersion = run.AgentVersion,
        ProviderProfileId = run.Route.ProfileId.Value,
        ProviderVersion = run.ProviderVersion,
        AttemptedProfileIdsJson = JsonSerializer.Serialize(
            run.AttemptedProfileIds.Select(item => item.ToString()).ToArray(),
            SerializerOptions),
        RequestId = run.RequestId.Value,
        ProviderType = run.Route.ProviderType,
        Model = run.Route.Model,
        IsFallback = run.Route.IsFallback,
        RequiredCapabilitiesJson = SerializeCapabilities(run.Route.RequiredCapabilities),
        SelectionEvidenceHash = run.Route.SelectionEvidenceHash,
        PlanEvidenceHash = run.PlanEvidenceHash,
        PreparedInputHash = run.PreparedInputHash,
        HealthEvidenceHash = run.HealthEvidenceHash,
        ContextRedactionCount = run.ContextRedactionCount,
        ContextPreparationPolicy = run.ContextPreparationPolicy,
        AdmissionRequestHash = run.AdmissionRequestHash,
        ReservedInputTokens = run.Reservation.InputTokens,
        ReservedOutputTokens = run.Reservation.OutputTokens,
        ReservedToolCalls = run.Reservation.ToolCalls,
        ReservedEvents = run.Reservation.Events,
        ReservedWallClockSeconds = run.Reservation.WallClockSeconds,
        LeaseOwner = run.Lease?.Owner,
        LeaseTokenHash = run.Lease?.TokenHash,
        LeaseAcquiredAtUtcTicks = run.Lease?.AcquiredAt.UtcTicks,
        LeaseHeartbeatAtUtcTicks = run.Lease?.HeartbeatAt.UtcTicks,
        LeaseExpiresAtUtcTicks = run.Lease?.ExpiresAt.UtcTicks,
        EventCount = run.StreamEvidence.EventCount,
        LastEventSequence = run.StreamEvidence.LastSequence,
        EventStreamHash = run.StreamEvidence.EventStreamHash,
        UsedInputTokens = run.Usage.InputTokens,
        UsedOutputTokens = run.Usage.OutputTokens,
        UsedToolCalls = run.Usage.ToolCalls,
        Cost = run.Usage.Cost,
        Currency = run.Usage.Currency,
        State = run.State.ToString(),
        CreatedAtUtcTicks = run.CreatedAt.UtcTicks,
        StartedAtUtcTicks = run.StartedAt?.UtcTicks,
        CompletedAtUtcTicks = run.CompletedAt?.UtcTicks,
        ActorId = run.ActorId.Value,
        IdempotencyKey = run.IdempotencyKey,
        CorrelationId = run.CorrelationId.Value,
        CausationId = run.CausationId?.Value,
        FinishReason = run.FinishReason?.ToString(),
        FailureCode = run.FailureCode?.ToString(),
        Version = run.Version,
    };

    private static ModelRunAttemptEntity Map(ModelRunAttemptRecord attempt) => new()
    {
        Id = attempt.Id.Value,
        RunId = attempt.RunId.Value,
        Sequence = attempt.Sequence,
        ProviderProfileId = attempt.Route.ProfileId.Value,
        ProviderVersion = attempt.ProviderVersion,
        ProviderType = attempt.Route.ProviderType,
        Model = attempt.Route.Model,
        IsFallback = attempt.Route.IsFallback,
        RequiredCapabilitiesJson = SerializeCapabilities(attempt.Route.RequiredCapabilities),
        SelectionEvidenceHash = attempt.Route.SelectionEvidenceHash,
        PlanEvidenceHash = attempt.PlanEvidenceHash,
        State = attempt.State.ToString(),
        CreatedAtUtcTicks = attempt.CreatedAt.UtcTicks,
        StartedAtUtcTicks = attempt.StartedAt?.UtcTicks,
        CompletedAtUtcTicks = attempt.CompletedAt?.UtcTicks,
        EventCount = attempt.StreamEvidence.EventCount,
        LastEventSequence = attempt.StreamEvidence.LastSequence,
        EventStreamHash = attempt.StreamEvidence.EventStreamHash,
        UsedInputTokens = attempt.Usage.InputTokens,
        UsedOutputTokens = attempt.Usage.OutputTokens,
        UsedToolCalls = attempt.Usage.ToolCalls,
        Cost = attempt.Usage.Cost,
        Currency = attempt.Usage.Currency,
        FinishReason = attempt.FinishReason?.ToString(),
        FailureCode = attempt.FailureCode?.ToString(),
        IsRetryable = attempt.IsRetryable,
        Version = attempt.Version,
    };

    private static ModelRunRecord Map(ModelRunEntity run) => new(
        new ModelRunId(run.Id),
        new InstallationId(run.InstallationId),
        run.InstallationVersion,
        new AgentIdentityId(run.AgentId),
        run.AgentVersion,
        run.ProviderVersion,
        Array.AsReadOnly((JsonSerializer.Deserialize<string[]>(
            run.AttemptedProfileIdsJson,
            SerializerOptions) ?? [])
            .Select(item => new ProviderProfileId(Guid.Parse(item)))
            .ToArray()),
        new ModelRequestId(run.RequestId),
        Route(
            run.ProviderProfileId,
            run.ProviderType,
            run.Model,
            run.IsFallback,
            run.RequiredCapabilitiesJson,
            run.SelectionEvidenceHash),
        run.PlanEvidenceHash,
        run.PreparedInputHash,
        run.HealthEvidenceHash,
        run.ContextRedactionCount,
        run.ContextPreparationPolicy,
        run.AdmissionRequestHash,
        new ModelRunBudgetReservation(
            run.ReservedInputTokens,
            run.ReservedOutputTokens,
            run.ReservedToolCalls,
            run.ReservedEvents,
            run.ReservedWallClockSeconds),
        run.LeaseOwner is null
            ? null
            : new ModelRunLease(
                run.LeaseOwner,
                run.LeaseTokenHash!,
                Timestamp(run.LeaseAcquiredAtUtcTicks!.Value),
                Timestamp(run.LeaseHeartbeatAtUtcTicks!.Value),
                Timestamp(run.LeaseExpiresAtUtcTicks!.Value)),
        new ModelRunStreamEvidence(run.EventCount, run.LastEventSequence, run.EventStreamHash),
        new ModelUsage(
            run.UsedInputTokens,
            run.UsedOutputTokens,
            run.UsedToolCalls,
            run.Cost,
            run.Currency),
        Enum.Parse<ModelRunState>(run.State, ignoreCase: false),
        Timestamp(run.CreatedAtUtcTicks),
        Timestamp(run.StartedAtUtcTicks),
        Timestamp(run.CompletedAtUtcTicks),
        new ActorId(run.ActorId),
        run.IdempotencyKey,
        new CorrelationId(run.CorrelationId),
        run.CausationId is null ? null : new CorrelationId(run.CausationId),
        run.FinishReason is null ? null : Enum.Parse<ModelFinishReason>(run.FinishReason, ignoreCase: false),
        run.FailureCode is null ? null : Enum.Parse<FailureCode>(run.FailureCode, ignoreCase: false),
        run.Version);

    private static ModelRunAttemptRecord Map(ModelRunAttemptEntity attempt) => new(
        new ModelRunAttemptId(attempt.Id),
        new ModelRunId(attempt.RunId),
        attempt.Sequence,
        attempt.ProviderVersion,
        Route(
            attempt.ProviderProfileId,
            attempt.ProviderType,
            attempt.Model,
            attempt.IsFallback,
            attempt.RequiredCapabilitiesJson,
            attempt.SelectionEvidenceHash),
        attempt.PlanEvidenceHash,
        Enum.Parse<ModelRunAttemptState>(attempt.State, ignoreCase: false),
        Timestamp(attempt.CreatedAtUtcTicks),
        Timestamp(attempt.StartedAtUtcTicks),
        Timestamp(attempt.CompletedAtUtcTicks),
        new ModelRunStreamEvidence(
            attempt.EventCount,
            attempt.LastEventSequence,
            attempt.EventStreamHash),
        new ModelUsage(
            attempt.UsedInputTokens,
            attempt.UsedOutputTokens,
            attempt.UsedToolCalls,
            attempt.Cost,
            attempt.Currency),
        attempt.FinishReason is null
            ? null
            : Enum.Parse<ModelFinishReason>(attempt.FinishReason, ignoreCase: false),
        attempt.FailureCode is null
            ? null
            : Enum.Parse<FailureCode>(attempt.FailureCode, ignoreCase: false),
        attempt.IsRetryable,
        attempt.Version);

    private static ModelRouteSelection Route(
        Guid profileId,
        string providerType,
        string model,
        bool isFallback,
        string capabilitiesJson,
        string selectionEvidenceHash) => new(
        new ProviderProfileId(profileId),
        providerType,
        model,
        isFallback,
        new ReadOnlySet<ModelCapability>(DeserializeCapabilities(capabilitiesJson).ToHashSet()),
        selectionEvidenceHash);

    private static string SerializeCapabilities(IReadOnlySet<ModelCapability> capabilities) =>
        JsonSerializer.Serialize(
            capabilities.OrderBy(item => item).Select(item => item.ToString()).ToArray(),
            SerializerOptions);

    private static ModelCapability[] DeserializeCapabilities(string value) =>
        (JsonSerializer.Deserialize<string[]>(value, SerializerOptions) ?? [])
            .Select(item => Enum.Parse<ModelCapability>(item, ignoreCase: false))
            .ToArray();

    private static DateTimeOffset Timestamp(long value) => new(value, TimeSpan.Zero);

    private static DateTimeOffset? Timestamp(long? value) =>
        value is null ? null : new DateTimeOffset(value.Value, TimeSpan.Zero);
}
