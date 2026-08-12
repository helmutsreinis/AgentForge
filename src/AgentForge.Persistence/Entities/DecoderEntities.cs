namespace AgentForge.Persistence.Entities;

internal sealed class DecoderProposalSnapshotEntity
{
    public Guid ProposalId { get; init; }
    public long Version { get; init; }
    public Guid InstallationId { get; init; }
    public required string DecoderId { get; init; }
    public required string State { get; init; }
    public required string CandidateHash { get; init; }
    public string? BaselineHash { get; init; }
    public required string PreviousSnapshotHash { get; init; }
    public required string SnapshotHash { get; init; }
    public long UpdatedAtUtcTicks { get; init; }
    public required string SnapshotJson { get; init; }
}

internal sealed class DecoderActiveVersionEntity
{
    public Guid InstallationId { get; init; }
    public required string DecoderId { get; init; }
    public required string CandidateHash { get; set; }
    public long Version { get; set; }
}
