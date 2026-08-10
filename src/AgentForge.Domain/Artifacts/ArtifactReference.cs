namespace AgentForge.Domain.Artifacts;

public sealed record ArtifactReference(
    string ContentHash,
    long Length,
    string MediaType,
    DateTimeOffset CreatedAt);
