using AgentForge.Domain.Agents;
using AgentForge.Domain.Primitives;

namespace AgentForge.Abstractions.Agents;

public interface IAgentIdentityRepository
{
    ValueTask AddAsync(AgentIdentity agent, CancellationToken cancellationToken);

    ValueTask UpdateAsync(
        AgentIdentity agent,
        long expectedVersion,
        CancellationToken cancellationToken);

    ValueTask<AgentIdentity?> FindByNameAsync(
        InstallationId installationId,
        string name,
        CancellationToken cancellationToken);

    ValueTask<AgentIdentity?> FindByIdAsync(
        AgentIdentityId agentId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<AgentIdentity>> ListAsync(
        InstallationId installationId,
        CancellationToken cancellationToken);
}

public interface IAgentDefinitionEvaluator
{
    DomainResult<AgentIdentityCandidate> NormalizeAndValidate(AgentIdentityCandidate candidate);

    DomainResult<EffectiveAgentDefinition> Evaluate(
        AgentIdentityCandidate normalizedCandidate,
        AgentForge.Domain.Providers.ProviderProfile providerProfile);
}
