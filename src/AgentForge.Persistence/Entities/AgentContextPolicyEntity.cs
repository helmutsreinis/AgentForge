namespace AgentForge.Persistence.Entities;

internal sealed class AgentContextPolicyEntity
{
    public Guid AgentId { get; set; }
    public long? DiscoveredContextWindowTokens { get; set; }
    public string? DiscoveredContextModel { get; set; }
    public long? ContextWindowOverrideTokens { get; set; }
    public bool ContextCompressionEnabled { get; set; } = true;
    public int ContextCompressionThresholdPercent { get; set; } = 80;
    public int ContextCompressionTargetPercent { get; set; } = 50;
    public int ContextProtectedRecentTurns { get; set; } = 4;
}
