using AgentForge.Domain.Agents;
using AgentForge.Domain.Models;
using AgentForge.Domain.Primitives;

namespace AgentForge.Abstractions.Models;

public interface IModelRouteAuthoritySnapshotReader
{
    Task<DomainResult<ModelRouteAuthoritySnapshot>> ReadAsync(
        InstallationId installationId,
        AgentIdentityId agentId,
        CancellationToken cancellationToken);
}
