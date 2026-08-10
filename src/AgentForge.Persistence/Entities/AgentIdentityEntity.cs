namespace AgentForge.Persistence.Entities;

internal sealed class AgentIdentityEntity
{
    public Guid Id { get; set; }
    public Guid InstallationId { get; set; }
    public required string Name { get; set; }
    public string? Expertise { get; set; }
    public string? Mission { get; set; }
    public required string PreferredLanguage { get; set; }
    public required string TimeZone { get; set; }
    public required string ResponseStyle { get; set; }
    public string? DefaultWorkspace { get; set; }
    public Guid PrimaryProviderProfileId { get; set; }
    public required string DataLocality { get; set; }
    public bool AllowFallback { get; set; }
    public required string MemoryScope { get; set; }
    public int MemoryRetentionDays { get; set; }
    public required string NetworkPosture { get; set; }
    public required string ToolGrantsJson { get; set; }
    public required string SkillGrantsJson { get; set; }
    public int MaxTurns { get; set; }
    public int MaxToolInvocations { get; set; }
    public long MaxInputTokens { get; set; }
    public long MaxOutputTokens { get; set; }
    public int MaxWallClockSeconds { get; set; }
    public int MaxChildDepth { get; set; }
    public int MaxChildren { get; set; }
    public int MaxChildConcurrency { get; set; }
    public long MaxChildTotalTokens { get; set; }
    public required string LearningMode { get; set; }
    public required string MutableSkillScope { get; set; }
    public long Version { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public required string ActorId { get; set; }
    public required string CorrelationId { get; set; }
}
