using AgentForge.Abstractions.Tools;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Tools;

namespace AgentForge.Tools;

internal sealed class RestrictedHostContainerRuntimeInvoker(RestrictedHostSandbox restrictedHost) : IContainerRuntimeInvoker
{
    public Task<DomainResult<ProcessExecutionResult>> InvokeAsync(
        ProcessExecutionRequest request,
        IProcessOutputObserver? observer,
        CancellationToken cancellationToken) => restrictedHost.ExecuteAsync(request, observer, cancellationToken);
}
