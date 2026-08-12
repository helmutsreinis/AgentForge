using AgentForge.Abstractions.Tools;

namespace AgentForge.Tools;

internal interface IProcessSandboxAdapter : ISandbox
{
}

internal interface IContainerRuntimeInvoker
{
    Task<AgentForge.Domain.Primitives.DomainResult<AgentForge.Domain.Tools.ProcessExecutionResult>> InvokeAsync(
        AgentForge.Domain.Tools.ProcessExecutionRequest request,
        IProcessOutputObserver? observer,
        CancellationToken cancellationToken);
}
