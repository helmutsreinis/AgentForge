namespace AgentForge.Persistence.Entities;

internal sealed class SearchProviderProfileEntity
{
    public Guid InstallationId { get; set; }
    public required string Id { get; set; }
    public required string Kind { get; set; }
    public required string Endpoint { get; set; }
    public required string SecretStore { get; set; }
    public required string SecretKey { get; set; }
    public bool IsEnabled { get; set; }
    public required string SafeSearch { get; set; }
    public required string CountryCode { get; set; }
    public required string SearchLanguage { get; set; }
    public long Version { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public required string ActorId { get; set; }
    public required string CorrelationId { get; set; }
}
