using AgentForge.Domain.Environments;
using AgentForge.Domain.Primitives;

namespace AgentForge.Abstractions.Environments;

public interface IEnvironmentProfiler
{
    Task<DomainResult<EnvironmentProfile>> CaptureAsync(
        CaptureEnvironmentRequest request,
        CancellationToken cancellationToken);
}

public interface IEnvironmentInventoryService
{
    Task<DomainResult<EnvironmentInventoryResult>> CaptureAsync(
        CaptureEnvironmentRequest request,
        CancellationToken cancellationToken);
}
