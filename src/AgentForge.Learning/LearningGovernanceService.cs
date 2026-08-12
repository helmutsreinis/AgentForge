using AgentForge.Abstractions.Auditing;
using AgentForge.Abstractions.Learning;
using AgentForge.Abstractions.Persistence;
using AgentForge.Abstractions.Skills;
using AgentForge.Abstractions.Time;
using AgentForge.Domain.Auditing;
using AgentForge.Domain.Learning;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Skills;

namespace AgentForge.Learning;

internal sealed class LearningGovernanceService(
    ILearningRepository repository,
    ISkillRegistryRepository skillRegistry,
    ISkillGovernanceService skillGovernance,
    IAuditRecorder audit,
    IUnitOfWork unitOfWork,
    IClock clock) : ILearningGovernanceService
{
    public async Task<DomainResult<LearningClassification>> CaptureAsync(
        CaptureLearningSignalRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var created = LearningSignalClassifier.Create(
            request.Id, request.InstallationId, request.Kind, request.RedactedSummary,
            request.SourceEvidenceHash, request.UsageReceipts, request.SuccessfulChain,
            request.OccurrenceCount, request.CapturedBy, clock.UtcNow,
            request.CorrelationId, request.CausationId);
        if (!created.IsSuccess) return DomainResult.Fail<LearningClassification>(created.Failure!);
        var classification = LearningSignalClassifier.Classify(created.Value);
        if (!classification.IsSuccess) return classification;
        try
        {
            await repository.AddSignalAsync(created.Value, classification.Value, cancellationToken);
            await RecordAsync(created.Value.InstallationId, created.Value.CapturedBy, created.Value.CorrelationId,
                created.Value.CausationId, "learning.signal-classified",
                new { SignalId = created.Value.Id.ToString(), created.Value.SignalHash },
                new { classification.Value.Action, classification.Value.ReasonCode, classification.Value.ClassificationHash },
                cancellationToken);
            return await CommitAsync(classification.Value, cancellationToken);
        }
        catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException)
        {
            return Conflict<LearningClassification>("The learning signal already exists or conflicts with durable evidence.");
        }
    }

    public async Task<DomainResult<LearningCandidate>> ProposeAsync(
        ProposeLearningCandidateRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var evidence = await repository.FindSignalAsync(request.SignalId, cancellationToken);
        if (evidence is null) return Invalid<LearningCandidate>("The classified learning signal does not exist.");
        var candidateVersion = await skillRegistry.FindAsync(
            evidence.Value.Signal.InstallationId, request.SkillId, request.CandidateVersion, cancellationToken);
        if (candidateVersion is null || candidateVersion.Status is not SkillPackageStatus.Installed ||
            candidateVersion.Provenance is not SkillPackageProvenance.AgentProposal)
            return Invalid<LearningCandidate>("The exact isolated agent-proposed skill package is not installed.");
        var baseline = await skillRegistry.FindActiveAsync(
            evidence.Value.Signal.InstallationId, request.SkillId, cancellationToken);
        var skillProposal = await skillGovernance.CreateProposalAsync(
            request.SkillProposalId, evidence.Value.Signal.InstallationId, request.SkillId,
            request.CandidateVersion, request.Roles.Proposer, evidence.Value.Signal.CorrelationId,
            evidence.Value.Signal.CausationId, cancellationToken);
        if (!skillProposal.IsSuccess) return DomainResult.Fail<LearningCandidate>(skillProposal.Failure!);
        var created = LearningCandidateStateMachine.Create(
            request.Id, evidence.Value.Signal, evidence.Value.Classification, request.SkillProposalId,
            request.SkillId, request.CandidateVersion, candidateVersion.Package.PackageHash,
            baseline?.Package.Version, baseline?.Package.PackageHash, request.ProposalWorkspace,
            candidateVersion.Package.Permissions, request.Roles, clock.UtcNow);
        if (!created.IsSuccess) return created;
        if (skillProposal.Value.CandidatePackageHash != created.Value.CandidatePackageHash ||
            skillProposal.Value.BaselinePackageHash != created.Value.BaselinePackageHash)
            return Conflict<LearningCandidate>("Skill governance authority changed during candidate creation.");
        return await AppendAsync(
            created.Value, null, request.Roles.Proposer, "learning.candidate-proposed", cancellationToken);
    }

    public Task<DomainResult<LearningCandidate>> VerifyAsync(
        LearningCandidateId id, long expectedVersion, ActorId verifier, LearningCandidateEvaluation evaluation,
        CancellationToken cancellationToken) => TransitionAsync(
            id, expectedVersion,
            async current =>
            {
                var next = LearningCandidateStateMachine.Verify(current, verifier, evaluation, clock.UtcNow);
                if (!next.IsSuccess) return next;
                var skill = await skillGovernance.EvaluateAsync(
                    current.SkillProposalId, current.Version,
                    new SkillEvaluationReceipt(
                        evaluation.TargetPassed, evaluation.HoldoutPassed,
                        evaluation.AdversarialPassed && evaluation.PermissionDiffApproved,
                        evaluation.BaselineScore, evaluation.CandidateScore,
                        evaluation.EvidenceHash), cancellationToken);
                return skill.IsSuccess
                    ? next
                    : DomainResult.Fail<LearningCandidate>(skill.Failure!);
            }, verifier, "learning.candidate-verified", cancellationToken);

    public Task<DomainResult<LearningCandidate>> CritiqueAsync(
        LearningCandidateId id, long expectedVersion, ActorId critic, LearningCritique critique,
        CancellationToken cancellationToken) => TransitionAsync(
            id, expectedVersion,
            current => Task.FromResult(LearningCandidateStateMachine.Critique(
                current, critic, critique, clock.UtcNow)),
            critic, "learning.candidate-critiqued", cancellationToken);

    public Task<DomainResult<LearningCandidate>> ApproveAsync(
        LearningCandidateId id, long expectedVersion, ActorId governor, CancellationToken cancellationToken) =>
        TransitionAsync(id, expectedVersion, async current =>
        {
            var baseline = await skillRegistry.FindActiveAsync(
                current.InstallationId, current.SkillId, cancellationToken);
            var next = LearningCandidateStateMachine.Approve(
                current, governor, baseline?.Package.PackageHash, clock.UtcNow);
            if (!next.IsSuccess) return next;
            var skill = await skillGovernance.ApproveAsync(
                current.SkillProposalId, 1, governor, cancellationToken);
            return skill.IsSuccess ? next : DomainResult.Fail<LearningCandidate>(skill.Failure!);
        }, governor, "learning.candidate-approved", cancellationToken);

    public Task<DomainResult<LearningCandidate>> StartCanaryAsync(
        LearningCandidateId id, long expectedVersion, ActorId governor, CancellationToken cancellationToken) =>
        TransitionAsync(id, expectedVersion, async current =>
        {
            var next = LearningCandidateStateMachine.StartCanary(current, governor, clock.UtcNow);
            if (!next.IsSuccess) return next;
            var skill = await skillGovernance.StartCanaryAsync(current.SkillProposalId, 2, cancellationToken);
            return skill.IsSuccess ? next : DomainResult.Fail<LearningCandidate>(skill.Failure!);
        }, governor, "learning.canary-started", cancellationToken);

    public Task<DomainResult<LearningCandidate>> FinishCanaryAsync(
        LearningCandidateId id, long expectedVersion, ActorId governor, bool passed,
        decimal baselineMetric, decimal candidateMetric, string evidenceHash,
        CancellationToken cancellationToken) => TransitionAsync(id, expectedVersion, async current =>
    {
        var next = LearningCandidateStateMachine.FinishCanary(
            current, governor, passed, baselineMetric, candidateMetric, evidenceHash, clock.UtcNow);
        if (!next.IsSuccess) return next;
        var skill = await skillGovernance.FinishCanaryAsync(
            current.SkillProposalId, 3,
            new SkillCanaryReceipt(passed, baselineMetric, candidateMetric, evidenceHash), cancellationToken);
        return skill.IsSuccess &&
            (skill.Value.State is SkillProposalState.Promoted) == (next.Value.State is LearningCandidateState.Promoted)
            ? next
            : skill.IsSuccess
                ? Conflict<LearningCandidate>("Learning and skill canary outcomes diverged.")
                : DomainResult.Fail<LearningCandidate>(skill.Failure!);
    }, governor, "learning.canary-finished", cancellationToken);

    public Task<DomainResult<LearningCandidate>> RollbackAsync(
        LearningCandidateId id, long expectedVersion, ActorId governor, string evidenceHash,
        CancellationToken cancellationToken) => TransitionAsync(id, expectedVersion, async current =>
    {
        var next = LearningCandidateStateMachine.Rollback(current, governor, evidenceHash, clock.UtcNow);
        if (!next.IsSuccess) return next;
        var skill = await skillGovernance.RollbackAsync(
            current.SkillProposalId, 4, evidenceHash, cancellationToken);
        return skill.IsSuccess ? next : DomainResult.Fail<LearningCandidate>(skill.Failure!);
    }, governor, "learning.candidate-rolled-back", cancellationToken);

    public async Task<DomainResult<SkillBundleDefinition>> SynthesizeBundleAsync(
        SynthesizeSkillBundleRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var evidence = await repository.FindSignalAsync(request.SignalId, cancellationToken);
        if (evidence is null) return Invalid<SkillBundleDefinition>("The classified bundle signal does not exist.");
        foreach (var step in evidence.Value.Signal.SuccessfulChain)
        {
            var exact = await skillRegistry.FindAsync(
                evidence.Value.Signal.InstallationId, step.SkillId, step.Version, cancellationToken);
            if (exact is null || exact.Package.PackageHash != step.PackageHash ||
                exact.Status is SkillPackageStatus.Quarantined or SkillPackageStatus.Archived)
                return Conflict<SkillBundleDefinition>("A pinned bundle skill is unavailable or changed.");
            if (!request.ExactPermissions.TryGetValue(step.SkillId, out var declared) ||
                !declared.Order(StringComparer.Ordinal).SequenceEqual(
                    exact.Package.Permissions.Order(StringComparer.Ordinal), StringComparer.Ordinal))
                return DomainResult.Fail<SkillBundleDefinition>(new DomainFailure(
                    FailureCode.PolicyDenied, "Bundle permissions must exactly match pinned skill authority."));
        }

        var bundle = SkillBundleSynthesizer.Synthesize(
            request.BundleId, request.Version, evidence.Value.Signal, evidence.Value.Classification,
            request.ExactPermissions, request.BaselineScore, request.CandidateScore,
            request.TargetPassed, request.HoldoutPassed, request.EvaluationEvidenceHash);
        if (!bundle.IsSuccess) return bundle;
        await repository.AddBundleAsync(bundle.Value, cancellationToken);
        await RecordAsync(evidence.Value.Signal.InstallationId, evidence.Value.Signal.CapturedBy,
            evidence.Value.Signal.CorrelationId, evidence.Value.Signal.CausationId,
            "learning.bundle-synthesized",
            new { BundleId = bundle.Value.Id.Value, bundle.Value.Version, bundle.Value.SourceSignalHash },
            new { bundle.Value.DefinitionHash, NodeCount = bundle.Value.Nodes.Count }, cancellationToken);
        return await CommitAsync(bundle.Value, cancellationToken);
    }

    private async Task<DomainResult<LearningCandidate>> TransitionAsync(
        LearningCandidateId id, long expectedVersion,
        Func<LearningCandidate, Task<DomainResult<LearningCandidate>>> transition,
        ActorId operationActor, string operation, CancellationToken cancellationToken)
    {
        var current = await repository.FindLatestCandidateAsync(id, cancellationToken);
        if (current is null) return Invalid<LearningCandidate>("The learning candidate does not exist.");
        if (current.Version != expectedVersion || !LearningCandidateStateMachine.IsConsistent(current))
            return Conflict<LearningCandidate>("The learning candidate version is stale or inconsistent.");
        var next = await transition(current);
        return next.IsSuccess
            ? await AppendAsync(next.Value, current.Version, operationActor, operation, cancellationToken)
            : next;
    }

    private async Task<DomainResult<LearningCandidate>> AppendAsync(
        LearningCandidate candidate, long? expectedVersion, ActorId operationActor, string operation,
        CancellationToken cancellationToken)
    {
        try
        {
            await repository.AppendCandidateAsync(candidate, expectedVersion, cancellationToken);
            await RecordAsync(candidate.InstallationId, operationActor,
                candidate.CorrelationId, candidate.CausationId, operation,
                new { CandidateId = candidate.Id.ToString(), candidate.Version, candidate.SignalHash },
                new { candidate.State, candidate.SnapshotHash }, cancellationToken);
            return await CommitAsync(candidate, cancellationToken);
        }
        catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException)
        {
            return Conflict<LearningCandidate>("The learning candidate append conflicted with durable state.");
        }
    }

    private async Task RecordAsync(
        InstallationId installationId, ActorId actorId, CorrelationId correlationId,
        CorrelationId? causationId, string operation, object input, object output,
        CancellationToken cancellationToken) => await audit.RecordAsync(new AuditRecordRequest(
            installationId, actorId, correlationId, causationId, operation, AuditOutcome.Succeeded,
            input, output, null), cancellationToken);

    private async Task<DomainResult<T>> CommitAsync<T>(T value, CancellationToken cancellationToken)
    {
        var commit = await unitOfWork.CommitAsync(cancellationToken);
        return commit.Succeeded ? DomainResult.Success(value) : DomainResult.Fail<T>(commit.Failure!);
    }

    private static DomainResult<T> Invalid<T>(string message) => DomainResult.Fail<T>(
        new DomainFailure(FailureCode.ValidationFailure, message));

    private static DomainResult<T> Conflict<T>(string message) => DomainResult.Fail<T>(
        new DomainFailure(FailureCode.ConcurrencyConflict, message));
}
