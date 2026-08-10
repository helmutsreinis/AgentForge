using AgentForge.Domain.Artifacts;

namespace AgentForge.Abstractions.Artifacts;

public interface IArtifactStore
{
    Task<ArtifactReference> PutAsync(
        Stream content,
        string mediaType,
        CancellationToken cancellationToken);

    Task<Stream> OpenReadAsync(
        ArtifactReference artifact,
        CancellationToken cancellationToken);
}
