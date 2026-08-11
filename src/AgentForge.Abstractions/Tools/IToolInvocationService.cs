using AgentForge.Domain.Primitives;
using AgentForge.Domain.Tools;

namespace AgentForge.Abstractions.Tools;

public interface IToolInvocationRepository
{
    ValueTask AddAsync(ToolInvocationRecord invocation, CancellationToken cancellationToken);

    ValueTask UpdateAsync(
        ToolInvocationRecord invocation,
        long expectedVersion,
        CancellationToken cancellationToken);

    ValueTask<ToolInvocationRecord?> FindByIdempotencyKeyAsync(
        InstallationId installationId,
        string idempotencyKey,
        CancellationToken cancellationToken);
}

public interface IToolInvocationService
{
    Task<DomainResult<ToolInvocationResult>> InvokeAsync(
        ToolInvocationRequest request,
        IProcessOutputObserver? observer,
        CancellationToken cancellationToken);
}

public interface IToolAvailabilityProbeService
{
    Task<DomainResult<ToolAvailabilityProbeResult>> ProbeAsync(
        ToolAvailabilityProbeRequest request,
        CancellationToken cancellationToken);
}
