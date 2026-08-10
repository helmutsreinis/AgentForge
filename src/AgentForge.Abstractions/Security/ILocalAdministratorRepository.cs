using AgentForge.Domain.Primitives;
using AgentForge.Domain.Security;

namespace AgentForge.Abstractions.Security;

public interface ILocalAdministratorRepository
{
    ValueTask AddAsync(LocalAdministrator administrator, CancellationToken cancellationToken);

    ValueTask<LocalAdministrator?> FindAsync(
        InstallationId installationId,
        CancellationToken cancellationToken);
}
