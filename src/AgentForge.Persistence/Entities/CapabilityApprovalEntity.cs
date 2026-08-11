namespace AgentForge.Persistence.Entities;

internal sealed class CapabilityApprovalEntity
{
    public Guid Id { get; set; }
    public Guid InstallationId { get; set; }
    public long InstallationVersion { get; set; }
    public Guid AgentId { get; set; }
    public long AgentVersion { get; set; }
    public required string RequestActorId { get; set; }
    public required string CapabilityId { get; set; }
    public required string RiskClass { get; set; }
    public string? ToolId { get; set; }
    public string? ToolVersion { get; set; }
    public required string ParametersHash { get; set; }
    public required string TargetKind { get; set; }
    public required string TargetHash { get; set; }
    public required string WorkspaceHash { get; set; }
    public required string RequestHash { get; set; }
    public required string Disposition { get; set; }
    public required string State { get; set; }
    public long CreatedAtUtcTicks { get; set; }
    public long ExpiresAtUtcTicks { get; set; }
    public required string DecidedBy { get; set; }
    public required string CorrelationId { get; set; }
    public required string PreviewHash { get; set; }
    public required string IdempotencyKey { get; set; }
    public long Version { get; set; }
    public long? ConsumedAtUtcTicks { get; set; }
    public long? RevokedAtUtcTicks { get; set; }
}
