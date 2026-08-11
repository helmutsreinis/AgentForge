namespace AgentForge.Persistence.Entities;

internal sealed class ModelRunEntity
{
    public Guid Id { get; set; }
    public Guid InstallationId { get; set; }
    public long InstallationVersion { get; set; }
    public Guid AgentId { get; set; }
    public long AgentVersion { get; set; }
    public Guid ProviderProfileId { get; set; }
    public long ProviderVersion { get; set; }
    public required string AttemptedProfileIdsJson { get; set; }
    public Guid RequestId { get; set; }
    public required string ProviderType { get; set; }
    public required string Model { get; set; }
    public bool IsFallback { get; set; }
    public required string RequiredCapabilitiesJson { get; set; }
    public required string SelectionEvidenceHash { get; set; }
    public required string PlanEvidenceHash { get; set; }
    public required string PreparedInputHash { get; set; }
    public required string HealthEvidenceHash { get; set; }
    public int ContextRedactionCount { get; set; }
    public required string ContextPreparationPolicy { get; set; }
    public required string AdmissionRequestHash { get; set; }
    public long ReservedInputTokens { get; set; }
    public long ReservedOutputTokens { get; set; }
    public int ReservedToolCalls { get; set; }
    public int ReservedEvents { get; set; }
    public int ReservedWallClockSeconds { get; set; }
    public string? LeaseOwner { get; set; }
    public string? LeaseTokenHash { get; set; }
    public long? LeaseAcquiredAtUtcTicks { get; set; }
    public long? LeaseHeartbeatAtUtcTicks { get; set; }
    public long? LeaseExpiresAtUtcTicks { get; set; }
    public int EventCount { get; set; }
    public long LastEventSequence { get; set; }
    public required string EventStreamHash { get; set; }
    public long UsedInputTokens { get; set; }
    public long UsedOutputTokens { get; set; }
    public int UsedToolCalls { get; set; }
    public decimal? Cost { get; set; }
    public string? Currency { get; set; }
    public required string State { get; set; }
    public long CreatedAtUtcTicks { get; set; }
    public long? StartedAtUtcTicks { get; set; }
    public long? CompletedAtUtcTicks { get; set; }
    public required string ActorId { get; set; }
    public required string IdempotencyKey { get; set; }
    public required string CorrelationId { get; set; }
    public string? CausationId { get; set; }
    public string? FinishReason { get; set; }
    public string? FailureCode { get; set; }
    public long Version { get; set; }
}
