using AgentForge.Domain.Persistence;
using AgentForge.Domain.Primitives;

namespace AgentForge.Abstractions.Persistence;

public interface IDatabaseBackupService
{
    Task<DomainResult<DatabaseBackupManifest>> CreateAsync(
        CreateDatabaseBackupRequest request,
        CancellationToken cancellationToken);

    Task<DomainResult<bool>> VerifyAsync(
        string backupDirectory,
        DatabaseBackupManifest manifest,
        CancellationToken cancellationToken);

    Task<DomainResult<bool>> RestoreAsync(
        RestoreDatabaseBackupRequest request,
        CancellationToken cancellationToken);
}
