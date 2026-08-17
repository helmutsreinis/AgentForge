namespace AgentForge.Persistence.Entities;

internal sealed class HttpApiProfileEntity
{
    public Guid InstallationId { get; set; }
    public required string ProfileId { get; set; }
    public required string DisplayName { get; set; }
    public required string BaseEndpoint { get; set; }
    public required string ProbeRelativePath { get; set; }
    public required string StaticHeadersJson { get; set; }
    public required string SecretStore { get; set; }
    public required string SecretKey { get; set; }
    public bool IsEnabled { get; set; }
    public long Version { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public required string ActorId { get; set; }
    public required string CorrelationId { get; set; }
}
