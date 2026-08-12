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
            request.SourceEvidenceHash, request.UsageReceipts, request.RevisionAuthorizations,
            request.SuccessfulChain,
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

    public async Task<DomainResult<SkillBundleProposal>> SynthesizeBundleAsync(
        SynthesizeSkillBundleRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var evidence = await repository.FindSignalAsync(request.SignalId, cancellationToken);
        if (evidence is null) return Invalid<SkillBundleProposal>("The classified bundle signal does not exist.");
        foreach (var step in evidence.Value.Signal.SuccessfulChain)
        {
            var exact = await skillRegistry.FindAsync(
                evidence.Value.Signal.InstallationId, step.SkillId, step.Version, cancellationToken);
            if (exact is null || exact.Package.PackageHash != step.PackageHash ||
                exact.Status is SkillPackageStatus.Quarantined or SkillPackageStatus.Archived)
                return Conflict<SkillBundleProposal>("A pinned bundle skill is unavailable or changed.");
            if (!request.ExactPermissions.TryGetValue(step.SkillId, out var declared) ||
                !declared.Order(StringComparer.Ordinal).SequenceEqual(
                    exact.Package.Permissions.Order(StringComparer.Ordinal), StringComparer.Ordinal))
                return DomainResult.Fail<SkillBundleProposal>(new DomainFailure(
                    FailureCode.PolicyDenied, "Bundle permissions must exactly match pinned skill authority."));
        }

        var bundle = SkillBundleSynthesizer.Synthesize(
            request.BundleId, request.Version, evidence.Value.Signal, evidence.Value.Classification,
            request.ExactPermissions, request.BaselineScore, request.CandidateScore,
            request.TargetPassed, request.HoldoutPassed, request.EvaluationEvidenceHash);
        if (!bundle.IsSuccess) return DomainResult.Fail<SkillBundleProposal>(bundle.Failure!);
        var proposal = SkillBundleProposalStateMachine.Create(
            request.ProposalId, evidence.Value.Signal.InstallationId, bundle.Value, request.Roles,
            request.ProposedBy, clock.UtcNow, evidence.Value.Signal.CorrelationId,
            evidence.Value.Signal.CausationId);
        if (!proposal.IsSuccess) return proposal;
        return await AppendBundleProposalAsync(
            proposal.Value, null, request.ProposedBy, "learning.bundle-proposed", cancellationToken);
    }

    public Task<DomainResult<SkillBundleProposal>> VerifyBundleAsync(
        SkillBundleProposalId id, long expectedVersion, ActorId verifier, string evidenceHash,
        CancellationToken cancellationToken) => TransitionBundleAsync(
            id, expectedVersion, verifier, "learning.bundle-verified",
            current => SkillBundleProposalStateMachine.Verify(current, verifier, evidenceHash, clock.UtcNow),
            cancellationToken);

    public Task<DomainResult<SkillBundleProposal>> CritiqueBundleAsync(
        SkillBundleProposalId id, long expectedVersion, ActorId critic, LearningCritique critique,
        CancellationToken cancellationToken) => TransitionBundleAsync(
            id, expectedVersion, critic, "learning.bundle-critiqued",
            current => SkillBundleProposalStateMachine.Critique(current, critic, critique, clock.UtcNow),
            cancellationToken);

    public async Task<DomainResult<SkillBundleProposal>> ApproveBundleAsync(
        SkillBundleProposalId id, long expectedVersion, ActorId governor, CancellationToken cancellationToken)
    {
        var loaded = await LoadBundleProposalAsync(id, expectedVersion, cancellationToken);
        if (!loaded.IsSuccess) return loaded;
        var current = loaded.Value;
        foreach (var node in current.Definition.Nodes)
        {
            var exact = await skillRegistry.FindAsync(
                current.InstallationId, node.SkillId, node.Version, cancellationToken);
            if (exact is null || exact.Package.PackageHash != node.PackageHash ||
                exact.Status is SkillPackageStatus.Quarantined or SkillPackageStatus.Archived)
                return Conflict<SkillBundleProposal>("A pinned bundle skill changed before governor activation.");
        }

        var next = SkillBundleProposalStateMachine.Approve(current, governor, clock.UtcNow);
        if (!next.IsSuccess) return next;
        await repository.AddBundleAsync(next.Value.Definition, cancellationToken);
        return await AppendBundleProposalAsync(
            next.Value, current.Version, governor, "learning.bundle-activated", cancellationToken);
    }

    public Task<DomainResult<SkillBundleProposal>> ArchiveBundleAsync(
        SkillBundleProposalId id, long expectedVersion, ActorId governor, CancellationToken cancellationToken) =>
        TransitionBundleAsync(
            id, expectedVersion, governor, "learning.bundle-archived",
            current => SkillBundleProposalStateMachine.Archive(current, governor, clock.UtcNow),
            cancellationToken);

    private async Task<DomainResult<SkillBundleProposal>> TransitionBundleAsync(
        SkillBundleProposalId id, long expectedVersion, ActorId actor, string operation,
        Func<SkillBundleProposal, DomainResult<SkillBundleProposal>> transition,
        CancellationToken cancellationToken)
    {
        var loaded = await LoadBundleProposalAsync(id, expectedVersion, cancellationToken);
        if (!loaded.IsSuccess) return loaded;
        var next = transition(loaded.Value);
        return next.IsSuccess
            ? await AppendBundleProposalAsync(next.Value, loaded.Value.Version, actor, operation, cancellationToken)
            : next;
    }

    private async Task<DomainResult<SkillBundleProposal>> LoadBundleProposalAsync(
        SkillBundleProposalId id, long expectedVersion, CancellationToken cancellationToken)
    {
        var current = await repository.FindLatestBundleProposalAsync(id, cancellationToken);
        return current is null
            ? Invalid<SkillBundleProposal>("The skill bundle proposal does not exist.")
            : current.Version != expectedVersion || !SkillBundleProposalStateMachine.IsConsistent(current)
                ? Conflict<SkillBundleProposal>("The skill bundle proposal version is stale or inconsistent.")
                : DomainResult.Success(current);
    }

    private async Task<DomainResult<SkillBundleProposal>> AppendBundleProposalAsync(
        SkillBundleProposal proposal, long? expectedVersion, ActorId actor, string operation,
        CancellationToken cancellationToken)
    {
        await repository.AppendBundleProposalAsync(proposal, expectedVersion, cancellationToken);
        await RecordAsync(proposal.InstallationId, actor,
            proposal.CorrelationId, proposal.CausationId, operation,
            new { ProposalId = proposal.Id.ToString(), proposal.Version, proposal.Definition.SourceSignalHash },
            new { proposal.State, proposal.SnapshotHash, proposal.Definition.DefinitionHash }, cancellationToken);
        return await CommitAsync(proposal, cancellationToken);
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
