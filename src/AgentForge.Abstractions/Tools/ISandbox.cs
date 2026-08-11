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

public interface IToolCatalog
{
    ValueTask<DomainResult<IReadOnlyList<ToolSummary>>> SearchAsync(
        ToolSearchRequest request,
        CancellationToken cancellationToken);

    ValueTask<DomainResult<ToolDescriptor>> DescribeAsync(
        string toolId,
        string version,
        CancellationToken cancellationToken);
}
