using AgentForge.Domain.Primitives;
using AgentForge.Domain.Tools;

namespace AgentForge.Abstractions.Tools;

public interface IProcessOutputObserver
{
    ValueTask ObserveAsync(ProcessOutputChunk chunk, CancellationToken cancellationToken);
}

public interface ISandbox
{
    ProcessSandboxCapabilities Capabilities { get; }

    Task<DomainResult<ProcessExecutionResult>> ExecuteAsync(
        ProcessExecutionRequest request,
        IProcessOutputObserver? observer,
        CancellationToken cancellationToken);
}
