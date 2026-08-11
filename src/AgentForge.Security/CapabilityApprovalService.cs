using System.Security.Cryptography;
using System.Text.Json;
using AgentForge.Abstractions.Agents;
using AgentForge.Abstractions.Auditing;
using AgentForge.Abstractions.Installations;
using AgentForge.Abstractions.Persistence;
using AgentForge.Abstractions.Security;
using AgentForge.Abstractions.Time;
using AgentForge.Domain.Agents;
using AgentForge.Domain.Auditing;
using AgentForge.Domain.Installations;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Security;
using Microsoft.Extensions.Options;

namespace AgentForge.Security;

internal sealed class CapabilityApprovalService(
    IInstallationRepository installations,
    IAgentIdentityRepository agents,
    ICapabilityApprovalRepository approvals,
    IAuthorizationContextFactory contextFactory,
    ICapabilityPolicyEvaluator policyEvaluator,
    ILocalAdministratorAuthenticator authenticator,
    ISensitiveDataRedactor redactor,
    IAuditRecorder auditRecorder,
    IUnitOfWork unitOfWork,
    IClock clock,
    IIdentifierGenerator identifiers,
    IOptions<SecurityOptions> options) : ICapabilityApprovalService
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly SecurityOptions _options = options.Value;

    public async Task<DomainResult<CapabilityApprovalPreview>> PreviewAsync(
        PreviewCapabilityApprovalRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var installation = await installations.ReadAsync(cancellationToken);
        var authorization = await AuthorizeAsync(
            installation,
            request.ApproverActorId,
            request.CorrelationId,
            request.AdministratorCredential,
            cancellationToken);
        if (!authorization.IsSuccess)
        {
            return DomainResult.Fail<CapabilityApprovalPreview>(authorization.Failure!);
        }

        if (request.ExpiresAt <= clock.UtcNow ||
            request.ExpiresAt > clock.UtcNow.AddMinutes(_options.MaximumApprovalLifetimeMinutes))
        {
            return InvalidPreview("Approval expiration must be in the future and within the configured lifetime.");
        }

        var context = contextFactory.Create(request.Invocation);
        if (!context.IsSuccess)
        {
            return DomainResult.Fail<CapabilityApprovalPreview>(context.Failure!);
        }

        if (context.Value.InstallationId != installation.Id ||
            context.Value.InstallationVersion != installation.Version)
        {
            return DeniedPreview("Authorization request does not match the current installation version.");
        }

        var agent = await agents.FindByIdAsync(context.Value.AgentId, cancellationToken);
        if (agent is null || agent.InstallationId != installation.Id || agent.Version != context.Value.AgentVersion)
        {
            return DeniedPreview("Authorization request does not match a current agent policy version.");
        }

        var policy = BuildPolicy(agent, context.Value);
        var evaluation = policyEvaluator.Evaluate(context.Value, policy, null, clock.UtcNow);
        if (evaluation.Decision is CapabilityDecision.Deny)
        {
            return DeniedPreview("Denied policy cannot be overridden by an approval.");
        }

        if (evaluation.Decision is CapabilityDecision.Allow)
        {
            return InvalidPreview("This exact request does not require approval.");
        }

        var previewHash = ComputePreviewHash(
            context.Value.RequestHash,
            policy.Fingerprint,
            request.Disposition,
            request.ExpiresAt,
            request.ApproverActorId,
            request.CorrelationId);
        using var parametersDocument = JsonDocument.Parse(context.Value.CanonicalParametersJson);
        var parameters = redactor.Redact(parametersDocument.RootElement);
        var target = redactor.Redact(context.Value.NormalizedTarget);
        var workspace = redactor.Redact(context.Value.NormalizedWorkspace);
        return DomainResult.Success(new CapabilityApprovalPreview(
            request.Disposition,
            request.ExpiresAt,
            context.Value.RequestHash,
            previewHash,
            parameters.Data,
            target.Data,
            workspace.Data,
            evaluation));
    }

    public async Task<DomainResult<CapabilityApproval>> ApplyAsync(
        ApplyCapabilityApprovalRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!IsBoundedIdentifier(request.IdempotencyKey, 256) || !IsSha256(request.PreviewHash))
        {
            return InvalidApproval("Apply requires a bounded idempotency key and canonical preview hash.");
        }

        var installation = await installations.ReadAsync(cancellationToken);
        var authorization = await AuthorizeAsync(
            installation,
            request.ApproverActorId,
            request.CorrelationId,
            request.AdministratorCredential,
            cancellationToken);
        if (!authorization.IsSuccess)
        {
            return DomainResult.Fail<CapabilityApproval>(authorization.Failure!);
        }

        var context = contextFactory.Create(request.Invocation);
        if (!context.IsSuccess)
        {
            return DomainResult.Fail<CapabilityApproval>(context.Failure!);
        }

        if (context.Value.InstallationId != installation.Id ||
            context.Value.InstallationVersion != installation.Version)
        {
            return DomainResult.Fail<CapabilityApproval>(new DomainFailure(
                FailureCode.PolicyDenied,
                "Authorization request does not match the current installation version."));
        }

        var existing = await approvals.FindByIdempotencyKeyAsync(
            installation.Id,
            request.IdempotencyKey,
            cancellationToken);
        if (existing is not null)
        {
            var isExactRetry = FixedEquals(existing.PreviewHash, request.PreviewHash) &&
                FixedEquals(existing.RequestHash, context.Value.RequestHash) &&
                existing.Disposition == request.Disposition &&
                existing.ExpiresAt == request.ExpiresAt &&
                existing.DecidedBy == request.ApproverActorId &&
                existing.CorrelationId == request.CorrelationId;
            return isExactRetry
                ? DomainResult.Success(existing)
                : DomainResult.Fail<CapabilityApproval>(new DomainFailure(
                    FailureCode.ConcurrencyConflict,
                    "Idempotency key is already bound to another approval request."));
        }

        var preview = await PreviewAsync(new PreviewCapabilityApprovalRequest(
            request.Invocation,
            request.Disposition,
            request.ExpiresAt,
            request.ApproverActorId,
            request.CorrelationId,
            request.AdministratorCredential), cancellationToken);
        if (!preview.IsSuccess)
        {
            return DomainResult.Fail<CapabilityApproval>(preview.Failure!);
        }

        if (!FixedEquals(preview.Value.PreviewHash, request.PreviewHash))
        {
            return DomainResult.Fail<CapabilityApproval>(new DomainFailure(
                FailureCode.PolicyDenied,
                "Approval apply does not match the exact preview."));
        }

        var created = CapabilityApprovalStateMachine.Create(
            new CapabilityApprovalId(identifiers.NewGuid()),
            context.Value,
            request.Disposition,
            clock.UtcNow,
            request.ExpiresAt,
            request.ApproverActorId,
            request.CorrelationId,
            preview.Value.PreviewHash,
            request.IdempotencyKey);
        if (!created.IsSuccess)
        {
            return created;
        }

        await approvals.AddAsync(created.Value, cancellationToken);
        await auditRecorder.RecordAsync(new AuditRecordRequest(
            created.Value.InstallationId,
            request.ApproverActorId,
            request.CorrelationId,
            request.Invocation.CausationId,
            request.Disposition is CapabilityApprovalDisposition.Grant
                ? "capability.approval-granted"
                : "capability.approval-denied",
            AuditOutcome.Succeeded,
            new
            {
                ApprovalId = created.Value.Id.ToString(),
                created.Value.InstallationVersion,
                created.Value.RequestHash,
                created.Value.PreviewHash,
                created.Value.CapabilityId,
                RiskClass = created.Value.RiskClass.ToString(),
                created.Value.ToolId,
                created.Value.ToolVersion,
                created.Value.ParametersHash,
                TargetKind = created.Value.TargetKind.ToString(),
                created.Value.TargetHash,
                created.Value.WorkspaceHash,
                Disposition = created.Value.Disposition.ToString(),
                created.Value.ExpiresAt,
                created.Value.IdempotencyKey,
            },
            new
            {
                State = created.Value.State.ToString(),
                created.Value.Version,
            },
            null), cancellationToken);
        var commit = await unitOfWork.CommitAsync(cancellationToken);
        return commit.Succeeded
            ? created
            : DomainResult.Fail<CapabilityApproval>(commit.Failure!);
    }

    private async Task<DomainResult<ActorId>> AuthorizeAsync(
        InstallationSnapshot installation,
        ActorId approverActorId,
        CorrelationId correlationId,
        ReadOnlyMemory<char> credential,
        CancellationToken cancellationToken)
    {
        if (installation.Id.Value == Guid.Empty || installation.State is not InstallationState.Ready ||
            !IsBoundedIdentifier(approverActorId.Value, 256) || !IsBoundedIdentifier(correlationId.Value, 128))
        {
            return DomainResult.Fail<ActorId>(new DomainFailure(
                FailureCode.PolicyDenied,
                "Capability approvals require a Ready installation and authenticated local administrator."));
        }

        var authenticated = await authenticator.AuthenticateAsync(
            installation.Id,
            credential,
            cancellationToken);
        return authenticated.IsSuccess && authenticated.Value == approverActorId
            ? authenticated
            : DomainResult.Fail<ActorId>(new DomainFailure(
                FailureCode.PolicyDenied,
                "Capability approval authentication failed."));
    }

    private static CapabilityPolicySnapshot BuildPolicy(
        AgentIdentity agent,
        AuthorizationContext context)
    {
        var isGranted = agent.CapabilityPolicy.ToolGrants.Contains(context.CapabilityId, StringComparer.Ordinal) ||
            agent.CapabilityPolicy.SkillGrants.Contains(context.CapabilityId, StringComparer.Ordinal);
        CapabilityPolicyRule[] rules = isGranted
            ? [new(
                context.CapabilityId,
                context.RiskClass,
                CapabilityDecision.RequireApproval,
                "Exact configured grants remain approval-gated.")]
            : [];
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            InstallationId = agent.InstallationId.ToString(),
            context.InstallationVersion,
            AgentId = agent.Id.ToString(),
            AgentVersion = agent.Version,
            Rules = rules,
        }, SerializerOptions);
        return new CapabilityPolicySnapshot(
            agent.InstallationId,
            context.InstallationVersion,
            agent.Id,
            agent.Version,
            rules,
            $"sha256:{Convert.ToHexStringLower(SHA256.HashData(bytes))}");
    }

    private static string ComputePreviewHash(
        string requestHash,
        string policyFingerprint,
        CapabilityApprovalDisposition disposition,
        DateTimeOffset expiresAt,
        ActorId approverActorId,
        CorrelationId correlationId)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            RequestHash = requestHash,
            PolicyFingerprint = policyFingerprint,
            Disposition = disposition.ToString(),
            ExpiresAt = expiresAt.ToUniversalTime(),
            ApproverActorId = approverActorId.Value,
            CorrelationId = correlationId.Value,
        }, SerializerOptions);
        return $"sha256:{Convert.ToHexStringLower(SHA256.HashData(bytes))}";
    }

    private static bool FixedEquals(string first, string second) =>
        first.Length == second.Length && CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.ASCII.GetBytes(first),
            System.Text.Encoding.ASCII.GetBytes(second));

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

    private static bool IsBoundedIdentifier(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength && !value.Any(char.IsControl);

    private static DomainResult<CapabilityApprovalPreview> InvalidPreview(string message) =>
        DomainResult.Fail<CapabilityApprovalPreview>(new DomainFailure(FailureCode.ValidationFailure, message));

    private static DomainResult<CapabilityApprovalPreview> DeniedPreview(string message) =>
        DomainResult.Fail<CapabilityApprovalPreview>(new DomainFailure(FailureCode.PolicyDenied, message));

    private static DomainResult<CapabilityApproval> InvalidApproval(string message) =>
        DomainResult.Fail<CapabilityApproval>(new DomainFailure(FailureCode.ValidationFailure, message));
}
