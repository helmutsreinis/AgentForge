using AgentForge.Domain.Primitives;
using AgentForge.Domain.Setup;

namespace AgentForge.Abstractions.Setup;

public interface ISetupProfileSnapshotRepository
{
    ValueTask AddAsync(SetupProfileSnapshot snapshot, CancellationToken cancellationToken);

    Task<IReadOnlyList<SetupProfileSnapshot>> ListAsync(
        InstallationId installationId,
        CancellationToken cancellationToken);
}
