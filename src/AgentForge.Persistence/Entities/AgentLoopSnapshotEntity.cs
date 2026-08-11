namespace AgentForge.Persistence.Entities;

internal sealed class AgentLoopSnapshotEntity
{
    public Guid LoopId { get; set; }
    public long Sequence { get; set; }
    public Guid InstallationId { get; set; }
    public Guid AgentId { get; set; }
    public long AgentVersion { get; set; }
    public int Turn { get; set; }
    public required string Phase { get; set; }
    public required string State { get; set; }
    public int MaximumTurns { get; set; }
    public int MaximumToolCalls { get; set; }
    public long MaximumInputTokens { get; set; }
    public long MaximumOutputTokens { get; set; }
    public int MaximumWallClockSeconds { get; set; }
    public int MaximumStructuredRepairs { get; set; }
    public int MaximumConsecutiveNoProgress { get; set; }
    public long UsedInputTokens { get; set; }
    public long UsedOutputTokens { get; set; }
    public int UsedToolCalls { get; set; }
    public int UsedWallClockSeconds { get; set; }
    public int StructuredRepairCount { get; set; }
    public int ConsecutiveNoProgress { get; set; }
    public bool CompletionPending { get; set; }
    public required string InitialStateHash { get; set; }
    public string? LastProgressEvidenceHash { get; set; }
    public required string StepEvidenceHash { get; set; }
    public required string PreviousSnapshotHash { get; set; }
    public required string SnapshotHash { get; set; }
    public long StartedAtUtcTicks { get; set; }
    public long UpdatedAtUtcTicks { get; set; }
    public required string ActorId { get; set; }
    public required string IdempotencyKey { get; set; }
    public required string CorrelationId { get; set; }
    public string? CausationId { get; set; }
    public string? FailureCode { get; set; }
}
