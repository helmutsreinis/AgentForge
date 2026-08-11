namespace AgentForge.Persistence.Entities;

internal sealed class ToolInvocationEntity
{
    public Guid Id { get; set; }
    public Guid InstallationId { get; set; }
    public long InstallationVersion { get; set; }
    public Guid AgentId { get; set; }
    public long AgentVersion { get; set; }
    public required string ActorId { get; set; }
    public required string ToolId { get; set; }
    public required string ToolVersion { get; set; }
    public required string ToolDescriptorHash { get; set; }
    public required string CapabilityId { get; set; }
    public required string RiskClass { get; set; }
    public required string ParametersHash { get; set; }
    public required string TargetKind { get; set; }
    public required string TargetHash { get; set; }
    public required string WorkspaceHash { get; set; }
    public required string RequestHash { get; set; }
    public Guid? ApprovalId { get; set; }
    public required string State { get; set; }
    public long CreatedAtUtcTicks { get; set; }
    public long? CompletedAtUtcTicks { get; set; }
    public required string IdempotencyKey { get; set; }
    public required string CorrelationId { get; set; }
    public string? CausationId { get; set; }
    public int? ExitCode { get; set; }
    public string? StandardOutputHash { get; set; }
    public int StandardOutputLength { get; set; }
    public string? StandardErrorHash { get; set; }
    public int StandardErrorLength { get; set; }
    public string? FailureCode { get; set; }
    public long Version { get; set; }
}
