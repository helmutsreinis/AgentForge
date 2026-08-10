using AgentForge.Domain.Installations;

namespace AgentForge.Abstractions.Installations;

public interface IInstallationStateReader
{
    ValueTask<InstallationSnapshot> ReadAsync(CancellationToken cancellationToken);
}
