using AgentForge.Domain.Artifacts;
using AgentForge.Domain.Learning;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Skills;

namespace AgentForge.Abstractions.Learning;

public interface ILearningRepository
{
    ValueTask AddSignalAsync(
        LearningSignal signal,
        LearningClassification classification,
        CancellationToken cancellationToken);

    ValueTask<(LearningSignal Signal, LearningClassification Classification)?> FindSignalAsync(
        LearningSignalId id,
        CancellationToken cancellationToken);

    ValueTask AppendCandidateAsync(
        LearningCandidate candidate,
        long? expectedVersion,
        CancellationToken cancellationToken);

    ValueTask<LearningCandidate?> FindLatestCandidateAsync(
        LearningCandidateId id,
        CancellationToken cancellationToken);

    ValueTask AddBundleAsync(SkillBundleDefinition bundle, CancellationToken cancellationToken);

    ValueTask AppendBundleProposalAsync(
        SkillBundleProposal proposal,
        long? expectedVersion,
        CancellationToken cancellationToken);

    ValueTask<SkillBundleProposal?> FindLatestBundleProposalAsync(
        SkillBundleProposalId id,
        CancellationToken cancellationToken);

    ValueTask<SkillBundleDefinition?> FindBundleAsync(
        SkillBundleId id,
        SkillVersion version,
        CancellationToken cancellationToken);
}

public sealed record CaptureLearningSignalRequest(
    LearningSignalId Id,
    InstallationId InstallationId,
    LearningSignalKind Kind,
    string RedactedSummary,
    string SourceEvidenceHash,
    IReadOnlyList<SkillUsageReceipt> UsageReceipts,
    IReadOnlyList<SkillRevisionAuthorization> RevisionAuthorizations,
    IReadOnlyList<SkillChainStep> SuccessfulChain,
    int OccurrenceCount,
    ActorId CapturedBy,
    CorrelationId CorrelationId,
    CorrelationId? CausationId);

public sealed record ProposeLearningCandidateRequest(
    LearningCandidateId Id,
    LearningSignalId SignalId,
    SkillProposalId SkillProposalId,
    SkillId SkillId,
    SkillVersion CandidateVersion,
    ArtifactReference ProposalWorkspace,
    LearningRoleAssignments Roles);

public sealed record SynthesizeSkillBundleRequest(
    SkillBundleProposalId ProposalId,
    SkillBundleId BundleId,
    SkillVersion Version,
    LearningSignalId SignalId,
    IReadOnlyDictionary<SkillId, IReadOnlyList<string>> ExactPermissions,
    decimal BaselineScore,
    decimal CandidateScore,
    bool TargetPassed,
    bool HoldoutPassed,
    string EvaluationEvidenceHash,
    LearningRoleAssignments Roles,
    ActorId ProposedBy);

public interface ILearningGovernanceService
{
    Task<DomainResult<LearningClassification>> CaptureAsync(
        CaptureLearningSignalRequest request,
        CancellationToken cancellationToken);

    Task<DomainResult<LearningCandidate>> ProposeAsync(
        ProposeLearningCandidateRequest request,
        CancellationToken cancellationToken);

    Task<DomainResult<LearningCandidate>> VerifyAsync(
        LearningCandidateId id,
        long expectedVersion,
        ActorId verifier,
        LearningCandidateEvaluation evaluation,
        CancellationToken cancellationToken);

    Task<DomainResult<LearningCandidate>> CritiqueAsync(
        LearningCandidateId id,
        long expectedVersion,
        ActorId critic,
        LearningCritique critique,
        CancellationToken cancellationToken);

    Task<DomainResult<LearningCandidate>> ApproveAsync(
        LearningCandidateId id,
        long expectedVersion,
        ActorId governor,
        CancellationToken cancellationToken);

    Task<DomainResult<LearningCandidate>> StartCanaryAsync(
        LearningCandidateId id,
        long expectedVersion,
        ActorId governor,
        CancellationToken cancellationToken);

    Task<DomainResult<LearningCandidate>> FinishCanaryAsync(
        LearningCandidateId id,
        long expectedVersion,
        ActorId governor,
        bool passed,
        decimal baselineMetric,
        decimal candidateMetric,
        string evidenceHash,
        CancellationToken cancellationToken);

    Task<DomainResult<LearningCandidate>> RollbackAsync(
        LearningCandidateId id,
        long expectedVersion,
        ActorId governor,
        string evidenceHash,
        CancellationToken cancellationToken);

    Task<DomainResult<SkillBundleProposal>> SynthesizeBundleAsync(
        SynthesizeSkillBundleRequest request,
        CancellationToken cancellationToken);

    Task<DomainResult<SkillBundleProposal>> VerifyBundleAsync(
        SkillBundleProposalId id,
        long expectedVersion,
        ActorId verifier,
        string evidenceHash,
        CancellationToken cancellationToken);

    Task<DomainResult<SkillBundleProposal>> CritiqueBundleAsync(
        SkillBundleProposalId id,
        long expectedVersion,
        ActorId critic,
        LearningCritique critique,
        CancellationToken cancellationToken);

    Task<DomainResult<SkillBundleProposal>> ApproveBundleAsync(
        SkillBundleProposalId id,
        long expectedVersion,
        ActorId governor,
        CancellationToken cancellationToken);

    Task<DomainResult<SkillBundleProposal>> ArchiveBundleAsync(
        SkillBundleProposalId id,
        long expectedVersion,
        ActorId governor,
        CancellationToken cancellationToken);
}
