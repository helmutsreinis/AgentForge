namespace AgentForge.Persistence.Entities;

internal sealed class LocalAdministratorEntity
{
    public Guid Id { get; set; }
    public Guid InstallationId { get; set; }
    public required string ActorId { get; set; }
    public required string SecretStore { get; set; }
    public required string SecretKey { get; set; }
    public required string VerifierAlgorithm { get; set; }
    public int VerifierWorkFactor { get; set; }
    public required string VerifierSalt { get; set; }
    public required string Verifier { get; set; }
    public long Version { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public required string CorrelationId { get; set; }
}
