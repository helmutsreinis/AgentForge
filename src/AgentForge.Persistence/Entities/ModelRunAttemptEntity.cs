namespace AgentForge.Persistence.Entities;

internal sealed class ModelRunAttemptEntity
{
    public Guid Id { get; set; }
    public Guid RunId { get; set; }
    public int Sequence { get; set; }
    public Guid ProviderProfileId { get; set; }
    public long ProviderVersion { get; set; }
    public required string ProviderType { get; set; }
    public required string Model { get; set; }
    public bool IsFallback { get; set; }
    public required string RequiredCapabilitiesJson { get; set; }
    public required string SelectionEvidenceHash { get; set; }
    public required string PlanEvidenceHash { get; set; }
    public required string State { get; set; }
    public long CreatedAtUtcTicks { get; set; }
    public long? StartedAtUtcTicks { get; set; }
    public long? CompletedAtUtcTicks { get; set; }
    public int EventCount { get; set; }
    public long LastEventSequence { get; set; }
    public required string EventStreamHash { get; set; }
    public long UsedInputTokens { get; set; }
    public long UsedOutputTokens { get; set; }
    public int UsedToolCalls { get; set; }
    public decimal? Cost { get; set; }
    public string? Currency { get; set; }
    public string? FinishReason { get; set; }
    public string? FailureCode { get; set; }
    public bool IsRetryable { get; set; }
    public long Version { get; set; }
}
