using AgentForge.Abstractions.Auditing;
using AgentForge.Abstractions.Persistence;
using AgentForge.Abstractions.Skills;
using AgentForge.Abstractions.Time;
using AgentForge.Domain.Auditing;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Skills;

namespace AgentForge.Skills;

internal sealed class SkillGovernanceService(
    ISkillRegistryRepository registry,
    ISkillProposalRepository proposals,
    IAuditRecorder audit,
    IUnitOfWork unitOfWork,
    IClock clock) : ISkillGovernanceService
{
    public async Task<DomainResult<SkillProposal>> CreateProposalAsync(
        SkillProposalId proposalId,
        InstallationId installationId,
        SkillId skillId,
        SkillVersion candidateVersion,
        ActorId proposedBy,
        CorrelationId correlationId,
        CorrelationId? causationId,
        CancellationToken cancellationToken)
    {
        var candidate = await registry.FindAsync(installationId, skillId, candidateVersion, cancellationToken);
        var baseline = await registry.FindActiveAsync(installationId, skillId, cancellationToken);
        if (candidate is null)
        {
            return Invalid("The exact installed candidate does not exist.");
        }

        var existing = await proposals.FindLatestAsync(proposalId, cancellationToken);
        if (existing is not null)
        {
            var exactReplay = SkillGovernanceStateMachine.IsConsistent(existing) &&
                existing.State is SkillProposalState.Proposed && existing.Version == 0 &&
                existing.InstallationId == installationId && existing.SkillId == skillId &&
                existing.CandidateVersion == candidateVersion &&
                existing.CandidatePackageHash == candidate.Package.PackageHash &&
                existing.BaselineVersion == baseline?.Package.Version &&
                existing.BaselinePackageHash == baseline?.Package.PackageHash &&
                existing.ProposedBy == proposedBy && existing.CorrelationId == correlationId &&
                existing.CausationId == causationId;
            return exactReplay
                ? DomainResult.Success(existing)
                : Conflict("The proposal ID is already bound to different or advanced governance state.");
        }

        var created = SkillGovernanceStateMachine.Create(
            proposalId,
            candidate,
            baseline,
            proposedBy,
            correlationId,
            causationId,
            clock.UtcNow);
        return !created.IsSuccess
            ? created
            : await AppendAndCommitAsync(created.Value, "skills.proposal-created", cancellationToken);
    }

    public async Task<DomainResult<SkillProposal>> EvaluateAsync(
        SkillProposalId proposalId,
        long expectedVersion,
        SkillEvaluationReceipt receipt,
        CancellationToken cancellationToken) => await TransitionAsync(
        proposalId,
        expectedVersion,
        current => SkillGovernanceStateMachine.Evaluate(current, receipt, clock.UtcNow),
        "skills.proposal-evaluated",
        cancellationToken);

    public async Task<DomainResult<SkillProposal>> ApproveAsync(
        SkillProposalId proposalId,
        long expectedVersion,
        ActorId approvedBy,
        CancellationToken cancellationToken) => await TransitionAsync(
        proposalId,
        expectedVersion,
        async current => SkillGovernanceStateMachine.Approve(
            current,
            approvedBy,
            (await registry.FindActiveAsync(current.InstallationId, current.SkillId, cancellationToken))
                ?.Package.PackageHash,
            clock.UtcNow),
        "skills.proposal-approved",
        cancellationToken);

    public async Task<DomainResult<SkillProposal>> StartCanaryAsync(
        SkillProposalId proposalId,
        long expectedVersion,
        CancellationToken cancellationToken) => await TransitionAsync(
        proposalId,
        expectedVersion,
        current => SkillGovernanceStateMachine.StartCanary(current, clock.UtcNow),
        "skills.canary-started",
        cancellationToken);

    public async Task<DomainResult<SkillProposal>> FinishCanaryAsync(
        SkillProposalId proposalId,
        long expectedVersion,
        SkillCanaryReceipt receipt,
        CancellationToken cancellationToken)
    {
        var loaded = await LoadCurrentAsync(proposalId, expectedVersion, cancellationToken);
        if (!loaded.IsSuccess)
        {
            return loaded;
        }

        var current = loaded.Value;
        var baseline = await registry.FindActiveAsync(current.InstallationId, current.SkillId, cancellationToken);
        var transitioned = SkillGovernanceStateMachine.FinishCanary(
            current,
            receipt,
            baseline?.Package.PackageHash,
            clock.UtcNow);
        if (!transitioned.IsSuccess)
        {
            return transitioned;
        }

        var candidate = await registry.FindAsync(
            current.InstallationId,
            current.SkillId,
            current.CandidateVersion,
            cancellationToken);
        if (candidate is null || candidate.Package.PackageHash != current.CandidatePackageHash ||
            candidate.Status is not SkillPackageStatus.Installed)
        {
            return Conflict("The candidate changed state before canary completion.");
        }

        if (transitioned.Value.State is SkillProposalState.Promoted)
        {
            if (baseline is not null)
            {
                await registry.UpdateAsync(baseline with
                {
                    Status = SkillPackageStatus.Installed,
                    RecordVersion = baseline.RecordVersion + 1,
                    UpdatedAt = clock.UtcNow,
                    ActorId = current.ApprovedBy!.Value,
                    CorrelationId = current.CorrelationId,
                }, baseline.RecordVersion, cancellationToken);
            }

            await registry.UpdateAsync(candidate with
            {
                Status = SkillPackageStatus.Active,
                RecordVersion = candidate.RecordVersion + 1,
                UpdatedAt = clock.UtcNow,
                ActorId = current.ApprovedBy!.Value,
                CorrelationId = current.CorrelationId,
            }, candidate.RecordVersion, cancellationToken);
        }
        else
        {
            await registry.UpdateAsync(candidate with
            {
                Status = SkillPackageStatus.Quarantined,
                RecordVersion = candidate.RecordVersion + 1,
                UpdatedAt = clock.UtcNow,
                ActorId = current.ApprovedBy!.Value,
                CorrelationId = current.CorrelationId,
            }, candidate.RecordVersion, cancellationToken);
        }

        return await AppendAndCommitAsync(
            transitioned.Value,
            transitioned.Value.State is SkillProposalState.Promoted
                ? "skills.candidate-promoted"
                : "skills.candidate-quarantined",
            cancellationToken);
    }

    public async Task<DomainResult<SkillProposal>> RollbackAsync(
        SkillProposalId proposalId,
        long expectedVersion,
        string evidenceHash,
        CancellationToken cancellationToken)
    {
        var loaded = await LoadCurrentAsync(proposalId, expectedVersion, cancellationToken);
        if (!loaded.IsSuccess)
        {
            return loaded;
        }

        var current = loaded.Value;
        var transitioned = SkillGovernanceStateMachine.Rollback(current, evidenceHash, clock.UtcNow);
        if (!transitioned.IsSuccess)
        {
            return transitioned;
        }

        var candidate = await registry.FindAsync(
            current.InstallationId,
            current.SkillId,
            current.CandidateVersion,
            cancellationToken);
        if (candidate is null || candidate.Status is not SkillPackageStatus.Active ||
            candidate.Package.PackageHash != current.CandidatePackageHash)
        {
            return Conflict("The promoted candidate is no longer the active version.");
        }

        RegisteredSkillVersion? baseline = null;
        if (current.BaselineVersion is { } baselineVersion)
        {
            baseline = await registry.FindAsync(
                current.InstallationId,
                current.SkillId,
                baselineVersion,
                cancellationToken);
            if (baseline is null || baseline.Status is not SkillPackageStatus.Installed ||
                baseline.Package.PackageHash != current.BaselinePackageHash)
            {
                return Conflict("The exact rollback baseline is unavailable.");
            }
        }

        await registry.UpdateAsync(candidate with
        {
            Status = SkillPackageStatus.Quarantined,
            RecordVersion = candidate.RecordVersion + 1,
            UpdatedAt = clock.UtcNow,
            ActorId = current.ApprovedBy!.Value,
            CorrelationId = current.CorrelationId,
        }, candidate.RecordVersion, cancellationToken);
        if (baseline is not null)
        {
            await registry.UpdateAsync(baseline with
            {
                Status = SkillPackageStatus.Active,
                RecordVersion = baseline.RecordVersion + 1,
                UpdatedAt = clock.UtcNow,
                ActorId = current.ApprovedBy!.Value,
                CorrelationId = current.CorrelationId,
            }, baseline.RecordVersion, cancellationToken);
        }

        return await AppendAndCommitAsync(transitioned.Value, "skills.candidate-rolled-back", cancellationToken);
    }

    private async Task<DomainResult<SkillProposal>> TransitionAsync(
        SkillProposalId proposalId,
        long expectedVersion,
        Func<SkillProposal, DomainResult<SkillProposal>> transition,
        string operation,
        CancellationToken cancellationToken)
    {
        var loaded = await LoadCurrentAsync(proposalId, expectedVersion, cancellationToken);
        if (!loaded.IsSuccess)
        {
            return loaded;
        }

        var next = transition(loaded.Value);
        return next.IsSuccess ? await AppendAndCommitAsync(next.Value, operation, cancellationToken) : next;
    }

    private async Task<DomainResult<SkillProposal>> TransitionAsync(
        SkillProposalId proposalId,
        long expectedVersion,
        Func<SkillProposal, Task<DomainResult<SkillProposal>>> transition,
        string operation,
        CancellationToken cancellationToken)
    {
        var loaded = await LoadCurrentAsync(proposalId, expectedVersion, cancellationToken);
        if (!loaded.IsSuccess)
        {
            return loaded;
        }

        var next = await transition(loaded.Value);
        return next.IsSuccess ? await AppendAndCommitAsync(next.Value, operation, cancellationToken) : next;
    }

    private async Task<DomainResult<SkillProposal>> LoadCurrentAsync(
        SkillProposalId proposalId,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        var current = await proposals.FindLatestAsync(proposalId, cancellationToken);
        return current is null
            ? Invalid("The skill proposal does not exist.")
            : current.Version != expectedVersion || !SkillGovernanceStateMachine.IsConsistent(current)
                ? Conflict("The skill proposal version is stale or inconsistent.")
                : DomainResult.Success(current);
    }

    private async Task<DomainResult<SkillProposal>> AppendAndCommitAsync(
        SkillProposal proposal,
        string operation,
        CancellationToken cancellationToken)
    {
        await proposals.AppendAsync(proposal, cancellationToken);
        await audit.RecordAsync(new AuditRecordRequest(
            proposal.InstallationId,
            proposal.ApprovedBy ?? proposal.ProposedBy,
            proposal.CorrelationId,
            proposal.CausationId,
            operation,
            AuditOutcome.Succeeded,
            new { ProposalId = proposal.Id.ToString(), proposal.Version },
            new { State = proposal.State.ToString(), proposal.SnapshotHash },
            null), cancellationToken);
        var commit = await unitOfWork.CommitAsync(cancellationToken);
        return commit.Succeeded ? DomainResult.Success(proposal) : DomainResult.Fail<SkillProposal>(commit.Failure!);
    }

    private static DomainResult<SkillProposal> Invalid(string message) =>
        DomainResult.Fail<SkillProposal>(new DomainFailure(FailureCode.ValidationFailure, message));

    private static DomainResult<SkillProposal> Conflict(string message) =>
        DomainResult.Fail<SkillProposal>(new DomainFailure(FailureCode.ConcurrencyConflict, message));
}
