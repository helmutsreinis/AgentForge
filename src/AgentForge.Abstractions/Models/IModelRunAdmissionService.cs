using AgentForge.Domain.Models;
using AgentForge.Domain.Primitives;

namespace AgentForge.Abstractions.Models;

public interface IModelRunAdmissionService
{
    Task<DomainResult<ModelRunAdmissionResult>> AdmitAsync(
        ModelRunAdmissionRequest request,
        CancellationToken cancellationToken);
}

public interface IModelRunRepository
{
    ValueTask AddAsync(ModelRunAggregate aggregate, CancellationToken cancellationToken);

    ValueTask UpdateAsync(
        ModelRunAggregate aggregate,
        long expectedRunVersion,
        long expectedAttemptVersion,
        CancellationToken cancellationToken);

    ValueTask AppendAttemptAsync(
        ModelRunAggregate aggregate,
        long expectedRunVersion,
        CancellationToken cancellationToken);

    ValueTask<ModelRunAggregate?> FindByIdAsync(
        ModelRunId runId,
        CancellationToken cancellationToken);

    ValueTask<ModelRunAggregate?> FindByIdempotencyKeyAsync(
        InstallationId installationId,
        string idempotencyKey,
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<ModelRunAttemptRecord>> ListAttemptsAsync(
        ModelRunId runId,
        CancellationToken cancellationToken);
}
