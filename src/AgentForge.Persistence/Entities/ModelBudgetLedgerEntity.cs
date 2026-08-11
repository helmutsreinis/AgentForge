namespace AgentForge.Persistence.Entities;

internal sealed class ModelBudgetLedgerEntity
{
    public Guid AgentId { get; set; }
    public Guid InstallationId { get; set; }
    public long AgentVersion { get; set; }
    public long ReservedInputTokens { get; set; }
    public long ReservedOutputTokens { get; set; }
    public int ReservedToolCalls { get; set; }
    public int ReservedEvents { get; set; }
    public int ReservedWallClockSeconds { get; set; }
    public int ActiveRuns { get; set; }
    public long ConsumedInputTokens { get; set; }
    public long ConsumedOutputTokens { get; set; }
    public long ConsumedToolCalls { get; set; }
    public long ConsumedEvents { get; set; }
    public long ConsumedWallClockSeconds { get; set; }
    public long CompletedRuns { get; set; }
    public long UpdatedAtUtcTicks { get; set; }
    public long Version { get; set; }
}
