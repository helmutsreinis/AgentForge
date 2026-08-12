using AgentForge.Domain.Auditing;
using AgentForge.Domain.Primitives;

namespace AgentForge.Abstractions.Auditing;

public interface ITrajectoryExporter
{
    Task<DomainResult<TrajectoryExportReceipt>> ExportAsync(
        TrajectoryExportRequest request,
        CancellationToken cancellationToken);
}

public interface ITrajectoryExportRepository
{
    Task<TrajectoryExportReceipt?> GetByIdempotencyAsync(
        InstallationId installationId,
        string idempotencyKey,
        CancellationToken cancellationToken);

    ValueTask AddAsync(TrajectoryExportReceipt receipt, CancellationToken cancellationToken);
}
