using AgentForge.Domain.Primitives;
using AgentForge.Domain.Setup;

namespace AgentForge.Abstractions.Setup;

public interface IRecoveryConfigurationInspector
{
    Task<DoctorCheck> InspectAsync(
        InstallationId installationId,
        CancellationToken cancellationToken);
}
