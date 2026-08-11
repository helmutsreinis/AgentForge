using AgentForge.Abstractions.Auditing;
using AgentForge.Abstractions.Orchestration;
using AgentForge.Abstractions.Persistence;
using AgentForge.Domain.Auditing;
using AgentForge.Domain.Orchestration;
using AgentForge.Domain.Primitives;

namespace AgentForge.Orchestration;

internal sealed class DelegationService(
    IDelegationPlanner planner,
    IDelegationGrantStore grants,
    IAuditRecorder audit,
    IUnitOfWork unitOfWork) : IDelegationService
{
    public async Task<DomainResult<DelegationResult>> CreateAsync(
        ParentDelegationAuthority parent,
        ChildDelegationRequest request,
        ActorId actorId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(request);
        var existing = await grants.FindAsync(request.Id, cancellationToken);
        if (existing is not null)
        {
            var replay = DelegationAuthorityEvaluator.Evaluate(parent, request, existing.IssuedAt);
            return replay.IsSuccess && string.Equals(
                    replay.Value.GrantHash,
                    existing.GrantHash,
                    StringComparison.Ordinal)
                ? DomainResult.Success(new DelegationResult(existing, true))
                : DomainResult.Fail<DelegationResult>(new DomainFailure(
                    FailureCode.ConcurrencyConflict,
                    "The delegation ID is already bound to different authority or intent."));
        }

        var evaluated = planner.Evaluate(parent, request);
        if (!evaluated.IsSuccess)
        {
            return DomainResult.Fail<DelegationResult>(evaluated.Failure!);
        }

        if (string.IsNullOrWhiteSpace(actorId.Value) || actorId.Value.Length > 256 ||
            actorId.Value.Any(char.IsControl))
        {
            return DomainResult.Fail<DelegationResult>(new DomainFailure(
                FailureCode.ValidationFailure,
                "Delegation actor identity is invalid."));
        }

        await grants.AddAsync(evaluated.Value, actorId, cancellationToken);
        await audit.RecordAsync(new AuditRecordRequest(
            evaluated.Value.InstallationId,
            actorId,
            evaluated.Value.CorrelationId,
            evaluated.Value.CausationId,
            "orchestration.delegation-created",
            AuditOutcome.Succeeded,
            new
            {
                DelegationId = evaluated.Value.Id.ToString(),
                ParentTaskId = evaluated.Value.ParentTaskId.ToString(),
                ParentAgentId = evaluated.Value.ParentAgentId.ToString(),
                ChildAgentId = evaluated.Value.ChildAgentId.ToString(),
                evaluated.Value.ParentAgentVersion,
                evaluated.Value.ChildAgentVersion,
            },
            new
            {
                evaluated.Value.GrantHash,
                evaluated.Value.Depth,
                CapabilityCount = evaluated.Value.CapabilityIds.Count,
                ContextEvidenceCount = evaluated.Value.ContextEvidenceHashes.Count,
                evaluated.Value.Budget,
                evaluated.Value.PolicySnapshotHash,
                evaluated.Value.SkillSnapshotHash,
            },
            null), cancellationToken);
        var commit = await unitOfWork.CommitAsync(cancellationToken);
        return commit.Succeeded
            ? DomainResult.Success(new DelegationResult(evaluated.Value, false))
            : DomainResult.Fail<DelegationResult>(commit.Failure!);
    }
}
