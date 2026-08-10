namespace AgentForge.Persistence.Entities;

internal sealed class ProviderProfileEntity
{
    public Guid Id { get; set; }
    public Guid InstallationId { get; set; }
    public required string Name { get; set; }
    public required string ProviderType { get; set; }
    public required string Endpoint { get; set; }
    public required string Model { get; set; }
    public required string SecretStore { get; set; }
    public required string SecretKey { get; set; }
    public bool TextGeneration { get; set; }
    public bool Streaming { get; set; }
    public bool ToolCalls { get; set; }
    public bool Images { get; set; }
    public required string EvidenceSource { get; set; }
    public long Version { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public required string ActorId { get; set; }
    public required string CorrelationId { get; set; }
}
