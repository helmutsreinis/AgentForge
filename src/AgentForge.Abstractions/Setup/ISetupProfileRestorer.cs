using AgentForge.Domain.Primitives;
using AgentForge.Domain.Setup;

namespace AgentForge.Abstractions.Setup;

public interface ISetupProfileRestorer
{
    Task<DomainResult<SetupProfileRestorePreview>> PreviewAsync(
        PreviewSetupProfileRestoreRequest request,
        CancellationToken cancellationToken);

    Task<DomainResult<SetupProfileRestoreResult>> ApplyAsync(
        ApplySetupProfileRestoreRequest request,
        CancellationToken cancellationToken);
}
