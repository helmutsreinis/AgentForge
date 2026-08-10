using AgentForge.Domain.Primitives;
using AgentForge.Domain.Providers;

namespace AgentForge.Abstractions.Providers;

public interface IProviderProfileRepository
{
    ValueTask AddAsync(ProviderProfile profile, CancellationToken cancellationToken);

    ValueTask UpdateAsync(
        ProviderProfile profile,
        long expectedVersion,
        CancellationToken cancellationToken);

    ValueTask<ProviderProfile?> FindByIdAsync(
        ProviderProfileId profileId,
        CancellationToken cancellationToken);

    ValueTask<ProviderProfile?> FindByNameAsync(
        InstallationId installationId,
        string name,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ProviderProfile>> ListAsync(
        InstallationId installationId,
        CancellationToken cancellationToken);
}

public interface IProviderProfileValidator
{
    Task<DomainResult<ProviderCapabilitySummary>> ValidateAsync(
        ProviderProfileCandidate candidate,
        CancellationToken cancellationToken);
}

public interface IProviderProfileDefinitionEvaluator
{
    DomainResult<ProviderProfileCandidate> NormalizeAndValidate(ProviderProfileCandidate candidate);
}
