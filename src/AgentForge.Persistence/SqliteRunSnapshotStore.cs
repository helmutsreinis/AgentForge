using AgentForge.Abstractions.Runtime;
using AgentForge.Domain.Agents;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Runtime;
using AgentForge.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace AgentForge.Persistence;

internal sealed class SqliteRunSnapshotStore(AgentForgeDbContext dbContext) : IRunSnapshotStore
{
    public async ValueTask AppendAsync(AgentLoopSnapshot snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!AgentLoopStateMachine.IsConsistent(snapshot))
        {
            throw new ArgumentException("Only a self-consistent agent-loop snapshot can be persisted.", nameof(snapshot));
        }

        await dbContext.AgentLoopSnapshots.AddAsync(Map(snapshot), cancellationToken);
    }

    public async ValueTask<AgentLoopSnapshot?> FindLatestAsync(
        AgentLoopId loopId,
        CancellationToken cancellationToken)
    {
        var entity = await dbContext.AgentLoopSnapshots.AsNoTracking()
            .Where(item => item.LoopId == loopId.Value)
            .OrderByDescending(item => item.Sequence)
            .FirstOrDefaultAsync(cancellationToken);
        return entity is null ? null : Map(entity);
    }

    public async ValueTask<AgentLoopSnapshot?> FindByIdempotencyKeyAsync(
        InstallationId installationId,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        var entity = await dbContext.AgentLoopSnapshots.AsNoTracking()
            .Where(item => item.InstallationId == installationId.Value && item.IdempotencyKey == idempotencyKey)
            .OrderByDescending(item => item.Sequence)
            .FirstOrDefaultAsync(cancellationToken);
        return entity is null ? null : Map(entity);
    }

    public async ValueTask<IReadOnlyList<AgentLoopSnapshot>> ListAsync(
        AgentLoopId loopId,
        CancellationToken cancellationToken)
    {
        var entities = await dbContext.AgentLoopSnapshots.AsNoTracking()
            .Where(item => item.LoopId == loopId.Value)
            .OrderBy(item => item.Sequence)
            .ToArrayAsync(cancellationToken);
        return entities.Select(Map).ToArray();
    }

    private static AgentLoopSnapshotEntity Map(AgentLoopSnapshot snapshot) => new()
    {
        LoopId = snapshot.LoopId.Value,
        Sequence = snapshot.Sequence,
        InstallationId = snapshot.InstallationId.Value,
        AgentId = snapshot.AgentId.Value,
        AgentVersion = snapshot.AgentVersion,
        Turn = snapshot.Turn,
        Phase = snapshot.Phase.ToString(),
        State = snapshot.State.ToString(),
        MaximumTurns = snapshot.Budget.MaximumTurns,
        MaximumToolCalls = snapshot.Budget.MaximumToolCalls,
        MaximumInputTokens = snapshot.Budget.MaximumInputTokens,
        MaximumOutputTokens = snapshot.Budget.MaximumOutputTokens,
        MaximumWallClockSeconds = snapshot.Budget.MaximumWallClockSeconds,
        MaximumStructuredRepairs = snapshot.Budget.MaximumStructuredRepairs,
        MaximumConsecutiveNoProgress = snapshot.Budget.MaximumConsecutiveNoProgress,
        UsedInputTokens = snapshot.Consumption.InputTokens,
        UsedOutputTokens = snapshot.Consumption.OutputTokens,
        UsedToolCalls = snapshot.Consumption.ToolCalls,
        UsedWallClockSeconds = snapshot.Consumption.WallClockSeconds,
        StructuredRepairCount = snapshot.StructuredRepairCount,
        ConsecutiveNoProgress = snapshot.ConsecutiveNoProgress,
        CompletionPending = snapshot.CompletionPending,
        InitialStateHash = snapshot.InitialStateHash,
        LastProgressEvidenceHash = snapshot.LastProgressEvidenceHash,
        StepEvidenceHash = snapshot.StepEvidenceHash,
        PreviousSnapshotHash = snapshot.PreviousSnapshotHash,
        SnapshotHash = snapshot.SnapshotHash,
        StartedAtUtcTicks = snapshot.StartedAt.UtcTicks,
        UpdatedAtUtcTicks = snapshot.UpdatedAt.UtcTicks,
        ActorId = snapshot.ActorId.Value,
        IdempotencyKey = snapshot.IdempotencyKey,
        CorrelationId = snapshot.CorrelationId.Value,
        CausationId = snapshot.CausationId?.Value,
        FailureCode = snapshot.FailureCode?.ToString(),
    };

    private static AgentLoopSnapshot Map(AgentLoopSnapshotEntity entity) => new(
        new AgentLoopId(entity.LoopId),
        new InstallationId(entity.InstallationId),
        new AgentIdentityId(entity.AgentId),
        entity.AgentVersion,
        entity.Sequence,
        entity.Turn,
        Enum.Parse<AgentLoopPhase>(entity.Phase, false),
        Enum.Parse<AgentLoopState>(entity.State, false),
        new AgentLoopBudget(
            entity.MaximumTurns,
            entity.MaximumToolCalls,
            entity.MaximumInputTokens,
            entity.MaximumOutputTokens,
            entity.MaximumWallClockSeconds,
            entity.MaximumStructuredRepairs,
            entity.MaximumConsecutiveNoProgress),
        new AgentLoopConsumption(
            entity.UsedInputTokens,
            entity.UsedOutputTokens,
            entity.UsedToolCalls,
            entity.UsedWallClockSeconds),
        entity.StructuredRepairCount,
        entity.ConsecutiveNoProgress,
        entity.CompletionPending,
        entity.InitialStateHash,
        entity.LastProgressEvidenceHash,
        entity.StepEvidenceHash,
        entity.PreviousSnapshotHash,
        entity.SnapshotHash,
        new DateTimeOffset(entity.StartedAtUtcTicks, TimeSpan.Zero),
        new DateTimeOffset(entity.UpdatedAtUtcTicks, TimeSpan.Zero),
        new ActorId(entity.ActorId),
        entity.IdempotencyKey,
        new CorrelationId(entity.CorrelationId),
        entity.CausationId is null ? null : new CorrelationId(entity.CausationId),
        entity.FailureCode is null ? null : Enum.Parse<FailureCode>(entity.FailureCode, false));
}
