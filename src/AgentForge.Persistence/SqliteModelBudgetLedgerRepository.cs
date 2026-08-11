using AgentForge.Abstractions.Models;
using AgentForge.Domain.Agents;
using AgentForge.Domain.Models;
using AgentForge.Domain.Primitives;
using AgentForge.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace AgentForge.Persistence;

internal sealed class SqliteModelBudgetLedgerRepository(AgentForgeDbContext dbContext)
    : IModelBudgetLedgerRepository
{
    public async ValueTask<ModelBudgetLedgerRecord?> FindAsync(
        AgentIdentityId agentId,
        CancellationToken cancellationToken)
    {
        var entity = await dbContext.ModelBudgetLedgers.AsNoTracking()
            .SingleOrDefaultAsync(item => item.AgentId == agentId.Value, cancellationToken);
        return entity is null ? null : Map(entity);
    }

    public async ValueTask AddAsync(ModelBudgetLedgerRecord ledger, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        await dbContext.ModelBudgetLedgers.AddAsync(Map(ledger), cancellationToken);
    }

    public async ValueTask UpdateAsync(
        ModelBudgetLedgerRecord ledger,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        var entity = Map(ledger);
        var tracked = dbContext.ModelBudgetLedgers.Local
            .SingleOrDefault(item => item.AgentId == entity.AgentId);
        if (tracked is not null)
        {
            dbContext.Entry(tracked).CurrentValues.SetValues(entity);
            dbContext.Entry(tracked).Property(item => item.Version).OriginalValue = expectedVersion;
            await ValueTask.CompletedTask;
            return;
        }

        dbContext.Attach(entity);
        dbContext.Entry(entity).State = EntityState.Modified;
        dbContext.Entry(entity).Property(item => item.Version).OriginalValue = expectedVersion;
        await ValueTask.CompletedTask;
    }

    private static ModelBudgetLedgerEntity Map(ModelBudgetLedgerRecord ledger) => new()
    {
        AgentId = ledger.AgentId.Value,
        InstallationId = ledger.InstallationId.Value,
        AgentVersion = ledger.AgentVersion,
        ReservedInputTokens = ledger.ActiveReservation.InputTokens,
        ReservedOutputTokens = ledger.ActiveReservation.OutputTokens,
        ReservedToolCalls = ledger.ActiveReservation.ToolCalls,
        ReservedEvents = ledger.ActiveReservation.Events,
        ReservedWallClockSeconds = ledger.ActiveReservation.WallClockSeconds,
        ActiveRuns = ledger.ActiveRuns,
        ConsumedInputTokens = ledger.Consumption.InputTokens,
        ConsumedOutputTokens = ledger.Consumption.OutputTokens,
        ConsumedToolCalls = ledger.Consumption.ToolCalls,
        ConsumedEvents = ledger.Consumption.Events,
        ConsumedWallClockSeconds = ledger.Consumption.WallClockSeconds,
        CompletedRuns = ledger.Consumption.CompletedRuns,
        UpdatedAtUtcTicks = ledger.UpdatedAt.UtcTicks,
        Version = ledger.Version,
    };

    private static ModelBudgetLedgerRecord Map(ModelBudgetLedgerEntity ledger) => new(
        new InstallationId(ledger.InstallationId),
        new AgentIdentityId(ledger.AgentId),
        ledger.AgentVersion,
        new ModelRunBudgetReservation(
            ledger.ReservedInputTokens,
            ledger.ReservedOutputTokens,
            ledger.ReservedToolCalls,
            ledger.ReservedEvents,
            ledger.ReservedWallClockSeconds),
        ledger.ActiveRuns,
        new ModelBudgetConsumption(
            ledger.ConsumedInputTokens,
            ledger.ConsumedOutputTokens,
            ledger.ConsumedToolCalls,
            ledger.ConsumedEvents,
            ledger.ConsumedWallClockSeconds,
            ledger.CompletedRuns),
        new DateTimeOffset(ledger.UpdatedAtUtcTicks, TimeSpan.Zero),
        ledger.Version);
}
