namespace AgentForge.Persistence.Entities;

internal sealed class SkillProposalSnapshotEntity
{
    public Guid ProposalId { get; set; }
    public long Version { get; set; }
    public Guid InstallationId { get; set; }
    public string SkillId { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string PreviousSnapshotHash { get; set; } = string.Empty;
    public string SnapshotHash { get; set; } = string.Empty;
    public string ProposalJson { get; set; } = string.Empty;
    public long UpdatedAtUtcTicks { get; set; }
    public string CorrelationId { get; set; } = string.Empty;
}
