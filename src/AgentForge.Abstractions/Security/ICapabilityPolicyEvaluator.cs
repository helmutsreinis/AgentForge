using AgentForge.Domain.Agents;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Security;

namespace AgentForge.Abstractions.Security;

public interface IAuthorizationContextFactory
{
    DomainResult<AuthorizationContext> Create(CapabilityInvocationRequest request);
}

public interface ICapabilityPolicyEvaluator
{
    CapabilityEvaluation Evaluate(
        AuthorizationContext context,
        CapabilityPolicySnapshot policy,
        CapabilityApproval? approval,
        DateTimeOffset evaluatedAt);

    CapabilityPolicySnapshot Intersect(
        CapabilityPolicySnapshot parent,
        CapabilityPolicySnapshot child);
}

public interface ICapabilityApprovalRepository
{
    ValueTask AddAsync(CapabilityApproval approval, CancellationToken cancellationToken);

    ValueTask UpdateAsync(
        CapabilityApproval approval,
        long expectedVersion,
        CancellationToken cancellationToken);

    ValueTask<CapabilityApproval?> FindByIdAsync(
        CapabilityApprovalId approvalId,
        CancellationToken cancellationToken);

    ValueTask<CapabilityApproval?> FindByIdempotencyKeyAsync(
        InstallationId installationId,
        string idempotencyKey,
        CancellationToken cancellationToken);

    ValueTask<CapabilityApproval?> FindLatestAsync(
        InstallationId installationId,
        AgentIdentityId agentId,
        string requestHash,
        CancellationToken cancellationToken);
}

public interface ICapabilityApprovalService
{
    Task<DomainResult<CapabilityApprovalPreview>> PreviewAsync(
        PreviewCapabilityApprovalRequest request,
        CancellationToken cancellationToken);

    Task<DomainResult<CapabilityApproval>> ApplyAsync(
        ApplyCapabilityApprovalRequest request,
        CancellationToken cancellationToken);
}
