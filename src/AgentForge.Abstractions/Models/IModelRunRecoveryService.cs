using AgentForge.Domain.Models;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Providers;

namespace AgentForge.Abstractions.Models;

public interface IModelRunRecoveryService
{
    Task<DomainResult<ModelRunHeartbeatResult>> HeartbeatAsync(
        ModelRunHeartbeatRequest request,
        CancellationToken cancellationToken);

    Task<DomainResult<ModelRunRecoveryResult>> RecoverExpiredAsync(
        ModelRunRecoveryRequest request,
        CancellationToken cancellationToken);
}

public interface IModelProviderHealthRepository
{
    ValueTask<ModelProviderHealthRecord?> FindAsync(
        ProviderProfileId profileId,
        CancellationToken cancellationToken);

    ValueTask AddAsync(ModelProviderHealthRecord record, CancellationToken cancellationToken);

    ValueTask UpdateAsync(
        ModelProviderHealthRecord record,
        long expectedVersion,
        CancellationToken cancellationToken);
}
