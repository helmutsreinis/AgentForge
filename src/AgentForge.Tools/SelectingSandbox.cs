using AgentForge.Abstractions.Tools;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Tools;

namespace AgentForge.Tools;

internal sealed class SelectingSandbox(
    RestrictedHostSandbox restrictedHost,
    DockerContainerSandbox container) : ISandbox
{
    public ProcessSandboxCapabilities Capabilities => restrictedHost.Capabilities;

    public Task<DomainResult<ProcessExecutionResult>> ExecuteAsync(
        ProcessExecutionRequest request,
        IProcessOutputObserver? observer,
        CancellationToken cancellationToken) => request.RequiredSandbox switch
        {
            ProcessSandboxKind.RestrictedHost => restrictedHost.ExecuteAsync(request, observer, cancellationToken),
            ProcessSandboxKind.Container => container.ExecuteAsync(request, observer, cancellationToken),
            _ => Task.FromResult(DomainResult.Fail<ProcessExecutionResult>(new DomainFailure(
                FailureCode.UnsupportedCapability, "Requested sandbox kind is unavailable."))),
        };
}
