using AgentForge.Domain.Agents;
using AgentForge.Domain.Models;
using AgentForge.Domain.Primitives;

namespace AgentForge.Abstractions.Models;

public interface IModelRunEventObserver
{
    ValueTask ObserveAsync(ModelStreamEvent modelEvent, CancellationToken cancellationToken);
}

public interface IModelRunExecutionService
{
    Task<DomainResult<ModelRunExecutionResult>> ExecuteAsync(
        ModelRunExecutionRequest request,
        IModelRunEventObserver? observer,
        CancellationToken cancellationToken);
}

public interface IModelBudgetLedgerRepository
{
    ValueTask<ModelBudgetLedgerRecord?> FindAsync(
        AgentIdentityId agentId,
        CancellationToken cancellationToken);

    ValueTask AddAsync(ModelBudgetLedgerRecord ledger, CancellationToken cancellationToken);

    ValueTask UpdateAsync(
        ModelBudgetLedgerRecord ledger,
        long expectedVersion,
        CancellationToken cancellationToken);
}
