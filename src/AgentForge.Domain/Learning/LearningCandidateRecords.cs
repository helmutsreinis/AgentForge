using System.Text;
using AgentForge.Domain.Artifacts;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Skills;

namespace AgentForge.Domain.Learning;

public readonly record struct LearningCandidateId(Guid Value)
{
    public override string ToString() => Value.ToString("D");
}

public enum LearningCandidateState
{
    Proposed,
    Verified,
    Critiqued,
    Approved,
    Canary,
    Promoted,
    Rejected,
    Quarantined,
    RolledBack,
}

public sealed record LearningCandidateEvaluation(
    bool TargetPassed,
    bool HoldoutPassed,
    bool AdversarialPassed,
    bool PermissionDiffApproved,
    decimal BaselineScore,
    decimal CandidateScore,
    string EvidenceHash);

public sealed record LearningCritique(bool Passed, IReadOnlyList<string> FindingCodes, string EvidenceHash);

public sealed record LearningCandidate(
    LearningCandidateId Id,
    InstallationId InstallationId,
    LearningSignalId SignalId,
    string SignalHash,
    LearningAction Action,
    SkillProposalId SkillProposalId,
    SkillId SkillId,
    SkillVersion CandidateVersion,
    string CandidatePackageHash,
    SkillVersion? BaselineVersion,
    string? BaselinePackageHash,
    ArtifactReference ProposalWorkspace,
    IReadOnlyList<string> RequestedPermissions,
    LearningRoleAssignments Roles,
    LearningCandidateState State,
    LearningCandidateEvaluation? Evaluation,
    LearningCritique? Critique,
    long Version,
    string PreviousSnapshotHash,
    string SnapshotHash,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    CorrelationId CorrelationId,
    CorrelationId? CausationId);

public static class LearningCandidateStateMachine
{
    public static DomainResult<LearningCandidate> Create(
        LearningCandidateId id,
        LearningSignal signal,
        LearningClassification classification,
        SkillProposalId skillProposalId,
        SkillId skillId,
        SkillVersion candidateVersion,
        string candidatePackageHash,
        SkillVersion? baselineVersion,
        string? baselinePackageHash,
        ArtifactReference proposalWorkspace,
        IReadOnlyList<string> requestedPermissions,
        LearningRoleAssignments roles,
        DateTimeOffset createdAt)
    {
        requestedPermissions ??= [];
        var usageAuthority = signal.UsageReceipts.Any(receipt => receipt.Succeeded && receipt.SkillId == skillId &&
            receipt.Version == baselineVersion && receipt.PackageHash == baselinePackageHash);
        if (!LearningSignalClassifier.IsConsistent(signal) || classification.SignalId != signal.Id ||
            classification.SignalHash != signal.SignalHash || !LearningValidation.IsHash(classification.ClassificationHash) ||
            classification.Action is not (LearningAction.NewSkill or LearningAction.SkillRevision) ||
            id.Value == Guid.Empty || skillProposalId.Value == Guid.Empty || !LearningValidation.IsSkillId(skillId) ||
            !SkillVersion.TryParse(candidateVersion.Value, out _) || !LearningValidation.IsHash(candidatePackageHash) ||
            classification.Action is LearningAction.SkillRevision &&
                (baselineVersion is null || !SkillVersion.TryParse(baselineVersion.Value.Value, out _) ||
                    !LearningValidation.IsHash(baselinePackageHash) || !usageAuthority) ||
            classification.Action is LearningAction.NewSkill && (baselineVersion is not null || baselinePackageHash is not null) ||
            proposalWorkspace.Length is < 1 or > 4_194_304 ||
            !LearningValidation.IsHash(proposalWorkspace.ContentHash) ||
            !string.Equals(proposalWorkspace.MediaType, "application/vnd.agentforge.learning-workspace+tar", StringComparison.Ordinal) ||
            requestedPermissions.Count > 128 || requestedPermissions.Any(value => !LearningValidation.IsBounded(value, 256)) ||
            requestedPermissions.Distinct(StringComparer.Ordinal).Count() != requestedPermissions.Count ||
            !roles.IsSeparated())
        {
            return Failure("A candidate requires isolated hashed evidence, exact usage authority, and five separated roles.");
        }

        var candidate = new LearningCandidate(
            id,
            signal.InstallationId,
            signal.Id,
            signal.SignalHash,
            classification.Action,
            skillProposalId,
            skillId,
            candidateVersion,
            candidatePackageHash,
            baselineVersion,
            baselinePackageHash,
            proposalWorkspace,
            requestedPermissions.Order(StringComparer.Ordinal).ToArray(),
            roles,
            LearningCandidateState.Proposed,
            null,
            null,
            0,
            LearningValidation.EmptyHash,
            LearningValidation.EmptyHash,
            createdAt,
            createdAt,
            signal.CorrelationId,
            signal.CausationId);
        return DomainResult.Success(candidate with { SnapshotHash = ComputeHash(candidate) });
    }

    public static DomainResult<LearningCandidate> Verify(
        LearningCandidate current,
        ActorId actor,
        LearningCandidateEvaluation evaluation,
        DateTimeOffset occurredAt)
    {
        if (!CanTransition(current, LearningCandidateState.Proposed, actor, LearningRole.Verifier, occurredAt) ||
            !IsValid(evaluation)) return Failure("Verification requires the assigned verifier and bounded evidence.");
        var passed = evaluation.TargetPassed && evaluation.HoldoutPassed && evaluation.AdversarialPassed &&
            evaluation.PermissionDiffApproved && evaluation.CandidateScore >= evaluation.BaselineScore;
        return Next(current, passed ? LearningCandidateState.Verified : LearningCandidateState.Rejected,
            occurredAt, evaluation: evaluation);
    }

    public static DomainResult<LearningCandidate> Critique(
        LearningCandidate current,
        ActorId actor,
        LearningCritique critique,
        DateTimeOffset occurredAt)
    {
        if (!CanTransition(current, LearningCandidateState.Verified, actor, LearningRole.Critic, occurredAt) ||
            !IsValid(critique)) return Failure("Critique requires the assigned critic and bounded deterministic evidence.");
        return Next(current, critique.Passed ? LearningCandidateState.Critiqued : LearningCandidateState.Rejected,
            occurredAt, critique: critique);
    }

    public static DomainResult<LearningCandidate> Approve(
        LearningCandidate current, ActorId actor, string? currentBaselineHash, DateTimeOffset occurredAt)
    {
        if (!CanTransition(current, LearningCandidateState.Critiqued, actor, LearningRole.Governor, occurredAt) ||
            current.BaselinePackageHash != currentBaselineHash)
            return DomainResult.Fail<LearningCandidate>(new DomainFailure(
                FailureCode.PolicyDenied, "Governor approval requires the exact current baseline hash."));
        return Next(current, LearningCandidateState.Approved, occurredAt);
    }

    public static DomainResult<LearningCandidate> StartCanary(
        LearningCandidate current, ActorId actor, DateTimeOffset occurredAt) =>
        !CanTransition(current, LearningCandidateState.Approved, actor, LearningRole.Governor, occurredAt)
            ? Failure("Only the assigned governor can start an approved canary.")
            : Next(current, LearningCandidateState.Canary, occurredAt);

    public static DomainResult<LearningCandidate> FinishCanary(
        LearningCandidate current, ActorId actor, bool passed, decimal baselineMetric, decimal candidateMetric,
        string evidenceHash, DateTimeOffset occurredAt)
    {
        if (!CanTransition(current, LearningCandidateState.Canary, actor, LearningRole.Governor, occurredAt) ||
            baselineMetric is < 0 or > 1_000_000 || candidateMetric is < 0 or > 1_000_000 ||
            !LearningValidation.IsHash(evidenceHash)) return Failure("Canary completion requires bounded governor evidence.");
        return Next(current, passed && candidateMetric >= baselineMetric
            ? LearningCandidateState.Promoted : LearningCandidateState.Quarantined, occurredAt);
    }

    public static DomainResult<LearningCandidate> Rollback(
        LearningCandidate current, ActorId actor, string evidenceHash, DateTimeOffset occurredAt) =>
        !CanTransition(current, LearningCandidateState.Promoted, actor, LearningRole.Governor, occurredAt) ||
        !LearningValidation.IsHash(evidenceHash)
            ? Failure("Only the assigned governor can roll back a promoted candidate with evidence.")
            : Next(current, LearningCandidateState.RolledBack, occurredAt);

    public static bool IsConsistent(LearningCandidate? candidate) => candidate is not null &&
        candidate.Id.Value != Guid.Empty && candidate.InstallationId.Value != Guid.Empty &&
        candidate.SignalId.Value != Guid.Empty && LearningValidation.IsHash(candidate.SignalHash) &&
        candidate.Action is LearningAction.NewSkill or LearningAction.SkillRevision &&
        candidate.SkillProposalId.Value != Guid.Empty &&
        LearningValidation.IsSkillId(candidate.SkillId) && SkillVersion.TryParse(candidate.CandidateVersion.Value, out _) &&
        LearningValidation.IsHash(candidate.CandidatePackageHash) &&
        (candidate.BaselineVersion is null || SkillVersion.TryParse(candidate.BaselineVersion.Value.Value, out _)) &&
        (candidate.BaselinePackageHash is null || LearningValidation.IsHash(candidate.BaselinePackageHash)) &&
        LearningValidation.IsHash(candidate.ProposalWorkspace.ContentHash) && candidate.ProposalWorkspace.Length > 0 &&
        candidate.RequestedPermissions.SequenceEqual(candidate.RequestedPermissions.Order(StringComparer.Ordinal)) &&
        candidate.RequestedPermissions.Distinct(StringComparer.Ordinal).Count() == candidate.RequestedPermissions.Count &&
        candidate.Roles.IsSeparated() && Enum.IsDefined(candidate.State) &&
        (candidate.Evaluation is null || IsValid(candidate.Evaluation)) &&
        (candidate.Critique is null || IsValid(candidate.Critique)) && candidate.Version >= 0 &&
        LearningValidation.IsHash(candidate.PreviousSnapshotHash) && LearningValidation.IsHash(candidate.SnapshotHash) &&
        candidate.UpdatedAt >= candidate.CreatedAt && LearningValidation.IsBounded(candidate.CorrelationId.Value, 128) &&
        string.Equals(candidate.SnapshotHash, ComputeHash(candidate), StringComparison.Ordinal);

    private static DomainResult<LearningCandidate> Next(
        LearningCandidate current, LearningCandidateState state, DateTimeOffset occurredAt,
        LearningCandidateEvaluation? evaluation = null, LearningCritique? critique = null)
    {
        var next = current with
        {
            State = state,
            Evaluation = evaluation ?? current.Evaluation,
            Critique = critique ?? current.Critique,
            Version = current.Version + 1,
            PreviousSnapshotHash = current.SnapshotHash,
            SnapshotHash = LearningValidation.EmptyHash,
            UpdatedAt = occurredAt,
        };
        return DomainResult.Success(next with { SnapshotHash = ComputeHash(next) });
    }

    private static bool CanTransition(
        LearningCandidate current, LearningCandidateState expected, ActorId actor, LearningRole role,
        DateTimeOffset occurredAt) => IsConsistent(current) && current.State == expected &&
        current.Roles.ActorFor(role) == actor && occurredAt >= current.UpdatedAt;

    private static bool IsValid(LearningCandidateEvaluation value) =>
        value.BaselineScore is >= 0 and <= 1_000_000 && value.CandidateScore is >= 0 and <= 1_000_000 &&
        LearningValidation.IsHash(value.EvidenceHash);

    private static bool IsValid(LearningCritique value) => value.FindingCodes.Count <= 128 &&
        value.FindingCodes.All(code => LearningValidation.IsBounded(code, 128)) &&
        value.FindingCodes.Distinct(StringComparer.Ordinal).Count() == value.FindingCodes.Count &&
        LearningValidation.IsHash(value.EvidenceHash);

    private static string ComputeHash(LearningCandidate value)
    {
        var builder = new StringBuilder(4096);
        foreach (var item in new object?[]
        {
            value.Id, value.InstallationId, value.SignalId, value.SignalHash, value.Action, value.SkillProposalId, value.SkillId,
            value.CandidateVersion, value.CandidatePackageHash, value.BaselineVersion?.Value ?? string.Empty,
            value.BaselinePackageHash ?? string.Empty, value.ProposalWorkspace.ContentHash,
            value.ProposalWorkspace.Length, value.ProposalWorkspace.MediaType, value.ProposalWorkspace.CreatedAt.UtcTicks,
        }) LearningValidation.Append(builder, item ?? string.Empty);
        foreach (var permission in value.RequestedPermissions) LearningValidation.Append(builder, permission);
        foreach (var actor in new[] { value.Roles.Worker, value.Roles.Proposer, value.Roles.Verifier,
                     value.Roles.Critic, value.Roles.Governor }) LearningValidation.Append(builder, actor);
        foreach (var item in new object?[]
        {
            value.State, value.Evaluation?.TargetPassed ?? false, value.Evaluation?.HoldoutPassed ?? false,
            value.Evaluation?.AdversarialPassed ?? false, value.Evaluation?.PermissionDiffApproved ?? false,
            value.Evaluation?.BaselineScore ?? 0, value.Evaluation?.CandidateScore ?? 0,
            value.Evaluation?.EvidenceHash ?? string.Empty, value.Critique?.Passed ?? false,
            value.Critique?.EvidenceHash ?? string.Empty,
        }) LearningValidation.Append(builder, item ?? string.Empty);
        foreach (var finding in value.Critique?.FindingCodes ?? []) LearningValidation.Append(builder, finding);
        foreach (var item in new object?[]
        {
            value.Version, value.PreviousSnapshotHash, value.CreatedAt.UtcTicks, value.UpdatedAt.UtcTicks,
            value.CorrelationId, value.CausationId?.Value ?? string.Empty,
        }) LearningValidation.Append(builder, item ?? string.Empty);
        return LearningValidation.Hash(builder.ToString());
    }

    private static DomainResult<LearningCandidate> Failure(string message) =>
        DomainResult.Fail<LearningCandidate>(new DomainFailure(FailureCode.ValidationFailure, message));
}
