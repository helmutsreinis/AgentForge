using AgentForge.Domain.Primitives;
using AgentForge.Domain.Skills;

namespace AgentForge.Abstractions.Skills;

public interface ISkillRegistryRepository
{
    ValueTask AddAsync(RegisteredSkillVersion version, CancellationToken cancellationToken);

    ValueTask UpdateAsync(
        RegisteredSkillVersion version,
        long expectedRecordVersion,
        CancellationToken cancellationToken);

    ValueTask<RegisteredSkillVersion?> FindAsync(
        InstallationId installationId,
        SkillId skillId,
        SkillVersion version,
        CancellationToken cancellationToken);

    ValueTask<RegisteredSkillVersion?> FindActiveAsync(
        InstallationId installationId,
        SkillId skillId,
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<RegisteredSkillVersion>> ListAsync(
        InstallationId installationId,
        CancellationToken cancellationToken);
}

public interface ISkillProposalRepository
{
    ValueTask AppendAsync(SkillProposal proposal, CancellationToken cancellationToken);

    ValueTask<SkillProposal?> FindLatestAsync(
        SkillProposalId proposalId,
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<SkillProposal>> ListLatestAsync(
        InstallationId installationId,
        int maximumResults,
        CancellationToken cancellationToken);
}

public interface ISkillRunSnapshotStore
{
    ValueTask AddAsync(SkillRunSnapshot snapshot, CancellationToken cancellationToken);

    ValueTask<SkillRunSnapshot?> FindAsync(
        SkillRunSnapshotId snapshotId,
        CancellationToken cancellationToken);

    ValueTask<SkillRunSnapshot?> FindByIdempotencyKeyAsync(
        InstallationId installationId,
        string idempotencyKey,
        CancellationToken cancellationToken);
}

public sealed record SkillInstallResult(RegisteredSkillVersion Version, bool WasReplay);

public sealed record SkillSearchResult(
    SkillId SkillId,
    SkillVersion Version,
    string Description,
    SkillPackageStatus Status,
    SkillPackageProvenance Provenance);

public interface ISkillRegistryService
{
    Task<DomainResult<SkillInstallResult>> InstallAsync(
        InstallationId installationId,
        string packageDirectory,
        SkillPackageProvenance provenance,
        ActorId actorId,
        CorrelationId correlationId,
        CancellationToken cancellationToken);

    Task<DomainResult<RegisteredSkillVersion>> SetStatusAsync(
        InstallationId installationId,
        SkillId skillId,
        SkillVersion version,
        long expectedRecordVersion,
        SkillPackageStatus status,
        ActorId actorId,
        CorrelationId correlationId,
        CancellationToken cancellationToken);

    Task<DomainResult<IReadOnlyList<SkillSearchResult>>> SearchAsync(
        InstallationId installationId,
        string query,
        int maximumResults,
        CancellationToken cancellationToken);
}

public interface ISkillGovernanceService
{
    Task<DomainResult<SkillProposal>> CreateProposalAsync(
        SkillProposalId proposalId,
        InstallationId installationId,
        SkillId skillId,
        SkillVersion candidateVersion,
        ActorId proposedBy,
        CorrelationId correlationId,
        CorrelationId? causationId,
        CancellationToken cancellationToken);

    Task<DomainResult<SkillProposal>> EvaluateAsync(
        SkillProposalId proposalId,
        long expectedVersion,
        SkillEvaluationReceipt receipt,
        CancellationToken cancellationToken);

    Task<DomainResult<SkillProposal>> ApproveAsync(
        SkillProposalId proposalId,
        long expectedVersion,
        ActorId approvedBy,
        CancellationToken cancellationToken);

    Task<DomainResult<SkillProposal>> StartCanaryAsync(
        SkillProposalId proposalId,
        long expectedVersion,
        CancellationToken cancellationToken);

    Task<DomainResult<SkillProposal>> FinishCanaryAsync(
        SkillProposalId proposalId,
        long expectedVersion,
        SkillCanaryReceipt receipt,
        CancellationToken cancellationToken);

    Task<DomainResult<SkillProposal>> RollbackAsync(
        SkillProposalId proposalId,
        long expectedVersion,
        string evidenceHash,
        CancellationToken cancellationToken);
}

public interface ISkillSnapshotService
{
    Task<DomainResult<SkillRunSnapshot>> CreateAsync(
        SkillRunSnapshotId snapshotId,
        InstallationId installationId,
        IReadOnlyList<SkillId> selectedSkillIds,
        ActorId actorId,
        string idempotencyKey,
        CorrelationId correlationId,
        CorrelationId? causationId,
        CancellationToken cancellationToken);

    Task<DomainResult<string>> OpenBodyAsync(
        SkillRunSnapshotId snapshotId,
        SkillId skillId,
        CancellationToken cancellationToken);
}
