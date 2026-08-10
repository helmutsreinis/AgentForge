using AgentForge.Domain.Installations;

namespace AgentForge.Abstractions.Installations;

public interface IInstallationRepository : IInstallationStateReader
{
    ValueTask AddAsync(InstallationSnapshot snapshot, CancellationToken cancellationToken);

    ValueTask UpdateAsync(
        InstallationSnapshot snapshot,
        long expectedVersion,
        CancellationToken cancellationToken);
}
