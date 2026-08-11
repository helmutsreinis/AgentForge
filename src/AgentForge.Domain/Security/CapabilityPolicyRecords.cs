using AgentForge.Domain.Agents;
using AgentForge.Domain.Auditing;
using AgentForge.Domain.Primitives;

namespace AgentForge.Domain.Security;

public enum CapabilityRiskClass
{
    Inventory,
    Read,
    Write,
    ExternalMutation,
    Credential,
    Privileged,
    Destructive,
    PhysicalControl,
}

public enum AuthorizationTargetKind
{
    None,
    FileSystemPath,
    Uri,
    Device,
    Recipient,
}

public enum CapabilityApprovalDisposition
{
    Grant,
    Deny,
}

public enum CapabilityApprovalState
{
    Active,
    Consumed,
    Revoked,
}

public readonly record struct CapabilityApprovalId(Guid Value)
{
    public override string ToString() => Value.ToString("D");
}

public sealed record CapabilityInvocationRequest(
    InstallationId InstallationId,
    long InstallationVersion,
    AgentIdentityId AgentId,
    long AgentVersion,
    ActorId ActorId,
    string CapabilityId,
    CapabilityRiskClass RiskClass,
    string? ToolId,
    string? ToolVersion,
    string ParametersJson,
    AuthorizationTargetKind TargetKind,
    string? Target,
    string? Workspace,
    CorrelationId CorrelationId,
    CorrelationId? CausationId = null);

public sealed record AuthorizationContext(
    InstallationId InstallationId,
    long InstallationVersion,
    AgentIdentityId AgentId,
    long AgentVersion,
    ActorId ActorId,
    string CapabilityId,
    CapabilityRiskClass RiskClass,
    string? ToolId,
    string? ToolVersion,
    string CanonicalParametersJson,
    string ParametersHash,
    AuthorizationTargetKind TargetKind,
    string? NormalizedTarget,
    string TargetHash,
    string? NormalizedWorkspace,
    string WorkspaceHash,
    CorrelationId CorrelationId,
    CorrelationId? CausationId,
    string RequestHash);

public sealed record CapabilityPolicyRule(
    string CapabilityId,
    CapabilityRiskClass RiskClass,
    CapabilityDecision Decision,
    string Reason);

public sealed record CapabilityPolicySnapshot(
    InstallationId InstallationId,
    long InstallationVersion,
    AgentIdentityId AgentId,
    long AgentVersion,
    IReadOnlyList<CapabilityPolicyRule> Rules,
    string Fingerprint);

public sealed record CapabilityEvaluation(
    CapabilityDecision Decision,
    string Reason,
    string RequestHash,
    CapabilityApprovalId? ApprovalId = null);

public sealed record CapabilityApproval(
    CapabilityApprovalId Id,
    InstallationId InstallationId,
    long InstallationVersion,
    AgentIdentityId AgentId,
    long AgentVersion,
    ActorId RequestActorId,
    string CapabilityId,
    CapabilityRiskClass RiskClass,
    string? ToolId,
    string? ToolVersion,
    string ParametersHash,
    AuthorizationTargetKind TargetKind,
    string TargetHash,
    string WorkspaceHash,
    string RequestHash,
    CapabilityApprovalDisposition Disposition,
    CapabilityApprovalState State,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    ActorId DecidedBy,
    CorrelationId CorrelationId,
    string PreviewHash,
    string IdempotencyKey,
    long Version,
    DateTimeOffset? ConsumedAt = null,
    DateTimeOffset? RevokedAt = null);

public sealed record PreviewCapabilityApprovalRequest(
    CapabilityInvocationRequest Invocation,
    CapabilityApprovalDisposition Disposition,
    DateTimeOffset ExpiresAt,
    ActorId ApproverActorId,
    CorrelationId CorrelationId,
    ReadOnlyMemory<char> AdministratorCredential);

public sealed record CapabilityApprovalPreview(
    CapabilityApprovalDisposition Disposition,
    DateTimeOffset ExpiresAt,
    string RequestHash,
    string PreviewHash,
    RedactedData Parameters,
    RedactedData Target,
    RedactedData Workspace,
    CapabilityEvaluation PolicyEvaluation);

public sealed record ApplyCapabilityApprovalRequest(
    CapabilityInvocationRequest Invocation,
    CapabilityApprovalDisposition Disposition,
    DateTimeOffset ExpiresAt,
    string PreviewHash,
    string IdempotencyKey,
    ActorId ApproverActorId,
    CorrelationId CorrelationId,
    ReadOnlyMemory<char> AdministratorCredential);

public static class CapabilityApprovalStateMachine
{
    public static DomainResult<CapabilityApproval> Create(
        CapabilityApprovalId id,
        AuthorizationContext context,
        CapabilityApprovalDisposition disposition,
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt,
        ActorId decidedBy,
        CorrelationId correlationId,
        string previewHash,
        string idempotencyKey)
    {
        if (id.Value == Guid.Empty || context.InstallationId.Value == Guid.Empty || context.InstallationVersion < 0 ||
            context.AgentId.Value == Guid.Empty || context.AgentVersion < 0 ||
            !Enum.IsDefined(context.RiskClass) || !Enum.IsDefined(context.TargetKind) ||
            !Enum.IsDefined(disposition) || !IsBounded(context.ActorId.Value, 256) ||
            !IsCapabilityId(context.CapabilityId) || !IsOptionalBounded(context.ToolId, 256) ||
            !IsOptionalBounded(context.ToolVersion, 128) || expiresAt <= createdAt ||
            !IsSha256(context.RequestHash) || !IsSha256(context.ParametersHash) ||
            !IsSha256(context.TargetHash) || !IsSha256(context.WorkspaceHash) || !IsSha256(previewHash) ||
            !IsBounded(decidedBy.Value, 256) || !IsBounded(correlationId.Value, 128) ||
            !IsBounded(idempotencyKey, 256))
        {
            return Invalid("Approval state requires exact bounded identities, hashes, and expiration.");
        }

        return DomainResult.Success(new CapabilityApproval(
            id,
            context.InstallationId,
            context.InstallationVersion,
            context.AgentId,
            context.AgentVersion,
            context.ActorId,
            context.CapabilityId,
            context.RiskClass,
            context.ToolId,
            context.ToolVersion,
            context.ParametersHash,
            context.TargetKind,
            context.TargetHash,
            context.WorkspaceHash,
            context.RequestHash,
            disposition,
            CapabilityApprovalState.Active,
            createdAt,
            expiresAt,
            decidedBy,
            correlationId,
            previewHash,
            idempotencyKey,
            0));
    }

    public static DomainResult<CapabilityApproval> Consume(
        CapabilityApproval approval,
        string requestHash,
        DateTimeOffset consumedAt)
    {
        ArgumentNullException.ThrowIfNull(approval);
        if (approval.Disposition is not CapabilityApprovalDisposition.Grant ||
            approval.State is not CapabilityApprovalState.Active ||
            consumedAt < approval.CreatedAt ||
            consumedAt >= approval.ExpiresAt ||
            !string.Equals(approval.RequestHash, requestHash, StringComparison.Ordinal))
        {
            return DomainResult.Fail<CapabilityApproval>(new DomainFailure(
                FailureCode.PolicyDenied,
                "Approval cannot authorize this request."));
        }

        return DomainResult.Success(approval with
        {
            State = CapabilityApprovalState.Consumed,
            ConsumedAt = consumedAt,
            Version = checked(approval.Version + 1),
        });
    }

    public static DomainResult<CapabilityApproval> Revoke(
        CapabilityApproval approval,
        DateTimeOffset revokedAt)
    {
        ArgumentNullException.ThrowIfNull(approval);
        if (approval.State is not CapabilityApprovalState.Active || revokedAt < approval.CreatedAt)
        {
            return DomainResult.Fail<CapabilityApproval>(new DomainFailure(
                FailureCode.InvalidStateTransition,
                "Only an active approval can be revoked."));
        }

        return DomainResult.Success(approval with
        {
            State = CapabilityApprovalState.Revoked,
            RevokedAt = revokedAt,
            Version = checked(approval.Version + 1),
        });
    }

    private static bool IsSha256(string value)
    {
        if (value.Length != 71 || !value.StartsWith("sha256:", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var character in value.AsSpan(7))
        {
            if (!char.IsAsciiDigit(character) && character is not (>= 'a' and <= 'f'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsBounded(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength && !value.Any(char.IsControl);

    private static bool IsOptionalBounded(string? value, int maximumLength) =>
        value is null || IsBounded(value, maximumLength);

    private static bool IsCapabilityId(string? value) =>
        IsBounded(value, 256) && value!.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or ':' or '_' or '-');

    private static DomainResult<CapabilityApproval> Invalid(string message) =>
        DomainResult.Fail<CapabilityApproval>(new DomainFailure(FailureCode.ValidationFailure, message));
}
