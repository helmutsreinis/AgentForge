namespace AgentForge.Persistence.Entities;

internal sealed class LearningSignalEntity
{
    public Guid Id { get; init; }
    public Guid InstallationId { get; init; }
    public required string Kind { get; init; }
    public required string Action { get; init; }
    public required string SignalHash { get; init; }
    public required string ClassificationHash { get; init; }
    public long CapturedAtUtcTicks { get; init; }
    public required string SignalJson { get; init; }
    public required string ClassificationJson { get; init; }
}

internal sealed class LearningCandidateSnapshotEntity
{
    public Guid Id { get; init; }
    public long Version { get; init; }
    public Guid InstallationId { get; init; }
    public Guid SignalId { get; init; }
    public Guid SkillProposalId { get; init; }
    public required string SkillId { get; init; }
    public required string State { get; init; }
    public required string CandidatePackageHash { get; init; }
    public string? BaselinePackageHash { get; init; }
    public required string PreviousSnapshotHash { get; init; }
    public required string SnapshotHash { get; init; }
    public long UpdatedAtUtcTicks { get; init; }
    public required string SnapshotJson { get; init; }
}

internal sealed class SkillBundleEntity
{
    public required string Id { get; init; }
    public required string Version { get; init; }
    public required string DefinitionHash { get; init; }
    public required string SourceSignalHash { get; init; }
    public required string DefinitionJson { get; init; }
}

internal sealed class SkillBundleProposalSnapshotEntity
{
    public Guid Id { get; init; }
    public long Version { get; init; }
    public Guid InstallationId { get; init; }
    public required string BundleId { get; init; }
    public required string BundleVersion { get; init; }
    public required string State { get; init; }
    public required string DefinitionHash { get; init; }
    public required string PreviousSnapshotHash { get; init; }
    public required string SnapshotHash { get; init; }
    public long UpdatedAtUtcTicks { get; init; }
    public required string SnapshotJson { get; init; }
}
