namespace AgentForge.Persistence.Entities;

internal sealed class ModelProviderHealthEntity
{
    public Guid ProfileId { get; set; }
    public Guid InstallationId { get; set; }
    public required string Status { get; set; }
    public required string Source { get; set; }
    public int ConsecutiveFailures { get; set; }
    public required string EvidenceCode { get; set; }
    public long ObservedAtUtcTicks { get; set; }
    public long ExpiresAtUtcTicks { get; set; }
    public long? RetryAfterUtcTicks { get; set; }
    public Guid LastRunId { get; set; }
    public Guid LastAttemptId { get; set; }
    public required string ActorId { get; set; }
    public required string CorrelationId { get; set; }
    public string? CausationId { get; set; }
    public long UpdatedAtUtcTicks { get; set; }
    public long Version { get; set; }
}
