using AgentForge.Abstractions.Runtime;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Runtime;

namespace AgentForge.Runtime;

internal sealed class UnavailableAgentLoopStepExecutor : IAgentLoopStepExecutor
{
    public Task<DomainResult<AgentLoopStepResult>> ExecuteAsync(
        AgentLoopSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(DomainResult.Fail<AgentLoopStepResult>(new DomainFailure(
            FailureCode.UnsupportedCapability,
            "No governed agent-loop step executor is configured.")));
    }
}
