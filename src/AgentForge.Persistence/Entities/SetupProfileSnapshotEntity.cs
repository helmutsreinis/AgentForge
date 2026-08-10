namespace AgentForge.Persistence.Entities;

internal sealed class SetupProfileSnapshotEntity
{
    public Guid Id { get; set; }
    public Guid InstallationId { get; set; }
    public long ProfileVersion { get; set; }
    public required string Kind { get; set; }
    public required string ArtifactContentHash { get; set; }
    public long ArtifactLength { get; set; }
    public required string ArtifactMediaType { get; set; }
    public DateTimeOffset ArtifactCreatedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public required string ActorId { get; set; }
    public required string CorrelationId { get; set; }
}
