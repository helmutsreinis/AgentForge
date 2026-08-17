using AgentForge.Abstractions.Agents;
using AgentForge.Abstractions.Auditing;
using AgentForge.Abstractions.Installations;
using AgentForge.Abstractions.Persistence;
using AgentForge.Abstractions.Security;
using AgentForge.Abstractions.Time;
using AgentForge.Abstractions.Tools;
using AgentForge.Domain.Agents;
using AgentForge.Domain.Auditing;
using AgentForge.Domain.Installations;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Security;
using AgentForge.Domain.Tools;

namespace AgentForge.Tools;

internal sealed class ToolInvocationService(
    IInstallationRepository installations,
    IAgentIdentityRepository agents,
    ICapabilityApprovalRepository approvals,
    IToolInvocationRepository invocations,
    IToolInvocationPlanner planner,
    ICapabilityPolicyFactory policyFactory,
    ICapabilityPolicyEvaluator policyEvaluator,
    ISensitiveDataRedactor redactor,
    IAuditRecorder auditRecorder,
    IUnitOfWork unitOfWork,
    IClock clock,
    IIdentifierGenerator identifiers,
    ISandbox sandbox,
    IBuiltInToolExecutor builtInExecutor) : IToolInvocationService
{
    public async Task<DomainResult<ToolInvocationResult>> InvokeAsync(
        ToolInvocationRequest request,
        IProcessOutputObserver? observer,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsBounded(request.IdempotencyKey, 256) ||
            redactor.Redact(new { request.IdempotencyKey }).ContainsRedactions)
        {
            return Invalid("Tool invocation idempotency is invalid.");
        }

        var planned = await planner.PlanAsync(new ToolInvocationPlanRequest(
            request.ExpectedInstallationVersion,
            request.AgentId,
            request.AgentVersion,
            request.ActorId,
            request.ToolId,
            request.ToolVersion,
            request.Parameters,
            request.Workspace,
            request.CorrelationId,
            request.CausationId), cancellationToken);
        if (!planned.IsSuccess)
        {
            return DomainResult.Fail<ToolInvocationResult>(planned.Failure!);
        }

        var descriptor = planned.Value.Descriptor;
        var authorization = planned.Value.Authorization;
        var installation = await installations.ReadAsync(cancellationToken);
        var agent = await agents.FindByIdAsync(request.AgentId, cancellationToken);
        if (installation.Id != authorization.InstallationId ||
            installation.State is not InstallationState.Ready ||
            installation.Version != authorization.InstallationVersion ||
            agent is null || agent.InstallationId != installation.Id ||
            agent.Version != authorization.AgentVersion)
        {
            return Denied("Tool invocation requires the exact current Ready installation and agent policy versions.");
        }

        var existing = await invocations.FindByIdempotencyKeyAsync(
            installation.Id,
            request.IdempotencyKey,
            cancellationToken);
        if (existing is not null)
        {
            return Replay(existing, authorization);
        }

        var approval = await approvals.FindLatestAsync(
            installation.Id,
            agent.Id,
            authorization.RequestHash,
            cancellationToken);
        var policy = policyFactory.Create(agent, authorization);
        var evaluation = policyEvaluator.Evaluate(authorization, policy, approval, clock.UtcNow);
        if (evaluation.Decision is CapabilityDecision.Deny)
        {
            return Denied(evaluation.Reason);
        }

        if (evaluation.Decision is CapabilityDecision.RequireApproval)
        {
            return DomainResult.Fail<ToolInvocationResult>(new DomainFailure(
                FailureCode.ApprovalRequired,
                evaluation.Reason));
        }

        CapabilityApprovalId? consumedApprovalId = null;
        if (evaluation.ApprovalId is not null)
        {
            if (approval is null || approval.Id != evaluation.ApprovalId)
            {
                return Denied("Policy approval evidence changed before invocation authorization.");
            }

            var consumed = CapabilityApprovalStateMachine.Consume(
                approval,
                authorization.RequestHash,
                clock.UtcNow);
            if (!consumed.IsSuccess)
            {
                return DomainResult.Fail<ToolInvocationResult>(consumed.Failure!);
            }

            await approvals.UpdateAsync(consumed.Value, approval.Version, cancellationToken);
            consumedApprovalId = consumed.Value.Id;
        }

        var authorized = ToolInvocationStateMachine.Authorize(
            new ToolInvocationId(identifiers.NewGuid()),
            authorization,
            descriptor.DescriptorHash,
            consumedApprovalId,
            request.IdempotencyKey,
            clock.UtcNow);
        if (!authorized.IsSuccess)
        {
            return DomainResult.Fail<ToolInvocationResult>(authorized.Failure!);
        }

        await invocations.AddAsync(authorized.Value, cancellationToken);
        await RecordAuthorizedAsync(authorized.Value, policy.Fingerprint, cancellationToken);
        var authorizationCommit = await unitOfWork.CommitAsync(cancellationToken);
        if (!authorizationCommit.Succeeded)
        {
            return DomainResult.Fail<ToolInvocationResult>(authorizationCommit.Failure!);
        }

        var currentInstallation = await installations.ReadAsync(cancellationToken);
        var currentAgent = await agents.FindByIdAsync(agent.Id, cancellationToken);
        var currentPolicyMatches = currentAgent is not null && FixedEquals(
            policy.Fingerprint,
            policyFactory.Create(currentAgent, authorization).Fingerprint);
        if (currentInstallation.Id != installation.Id || currentInstallation.State is not InstallationState.Ready ||
            currentInstallation.Version != installation.Version || currentAgent is null ||
            currentAgent.InstallationId != installation.Id || currentAgent.Version != agent.Version ||
            !currentPolicyMatches)
        {
            var failure = new DomainFailure(
                FailureCode.PolicyDenied,
                "Installation or agent policy changed after authorization; the consumed grant will not be replayed.");
            var failed = ToolInvocationStateMachine.Fail(
                authorized.Value,
                failure,
                TimestampAtOrAfter(authorized.Value));
            var persisted = await PersistFinalAsync(
                failed.Value,
                AuditOutcome.Denied,
                failure.Code.ToString(),
                cancellationToken);
            return persisted.IsSuccess
                ? DomainResult.Fail<ToolInvocationResult>(failure)
                : DomainResult.Fail<ToolInvocationResult>(persisted.Failure!);
        }

        try
        {
            var execution = descriptor.Definition.ExecutionKind is ToolExecutionKind.BuiltIn
                ? await builtInExecutor.ExecuteAsync(new BuiltInToolExecutionRequest(
                    descriptor.Definition.BuiltInHandlerId!,
                    request.Parameters,
                    authorization.NormalizedWorkspace!,
                    authorization.NormalizedTarget,
                    descriptor.Definition.Process.MaximumOutputBytes), cancellationToken)
                : await sandbox.ExecuteAsync(new ProcessExecutionRequest(
                    descriptor.Definition.Process.ExecutablePath,
                    planned.Value.Arguments,
                    authorization.NormalizedWorkspace!,
                    authorization.NormalizedWorkspace!,
                    new Dictionary<string, string>(StringComparer.Ordinal),
                    TimeSpan.FromSeconds(descriptor.Definition.Process.TimeoutSeconds),
                    descriptor.Definition.Process.MaximumOutputBytes,
                    descriptor.Definition.Process.NetworkPolicy,
                    descriptor.Definition.Process.RequiredSandbox,
                    descriptor.Definition.Process.RequiredFeatures,
                    descriptor.Definition.SideEffects.HasFlag(ToolSideEffectKind.WritesFileSystem)
                        ? ProcessFileSystemPolicy.ReadWriteWorkspace
                        : ProcessFileSystemPolicy.ReadOnlyWorkspace), observer, cancellationToken);
            if (!execution.IsSuccess)
            {
                var failure = execution.Failure!;
                var failed = ToolInvocationStateMachine.Fail(
                    authorized.Value,
                    failure,
                    TimestampAtOrAfter(authorized.Value));
                var persisted = await PersistFinalAsync(
                    failed.Value,
                    AuditOutcome.Failed,
                    failure.Code.ToString(),
                    cancellationToken);
                return persisted.IsSuccess
                    ? DomainResult.Fail<ToolInvocationResult>(failure)
                    : DomainResult.Fail<ToolInvocationResult>(persisted.Failure!);
            }

            var completed = ToolInvocationStateMachine.Complete(authorized.Value, execution.Value);
            if (!completed.IsSuccess)
            {
                var failure = new DomainFailure(
                    FailureCode.RecoverableExternalFailure,
                    "Sandbox returned invalid process completion evidence.",
                    true);
                var failed = ToolInvocationStateMachine.Fail(
                    authorized.Value,
                    failure,
                    TimestampAtOrAfter(authorized.Value));
                var persisted = await PersistFinalAsync(
                    failed.Value,
                    AuditOutcome.Failed,
                    failure.Code.ToString(),
                    cancellationToken);
                return persisted.IsSuccess
                    ? DomainResult.Fail<ToolInvocationResult>(failure)
                    : DomainResult.Fail<ToolInvocationResult>(persisted.Failure!);
            }

            var completion = await PersistFinalAsync(
                completed.Value,
                execution.Value.ExitCode == 0 ? AuditOutcome.Succeeded : AuditOutcome.Failed,
                execution.Value.ExitCode == 0 ? null : FailureCode.RecoverableExternalFailure.ToString(),
                cancellationToken);
            return completion.IsSuccess
                ? DomainResult.Success(new ToolInvocationResult(
                    completion.Value,
                    false,
                    execution.Value.StandardOutput,
                    execution.Value.StandardError,
                    execution.Value.Sandbox))
                : DomainResult.Fail<ToolInvocationResult>(completion.Failure!);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var canceled = ToolInvocationStateMachine.Cancel(
                authorized.Value,
                TimestampAtOrAfter(authorized.Value));
            _ = await PersistFinalAsync(canceled.Value, AuditOutcome.Canceled, null, CancellationToken.None);
            throw;
        }
    }

    private async Task<DomainResult<ToolInvocationRecord>> PersistFinalAsync(
        ToolInvocationRecord invocation,
        AuditOutcome outcome,
        string? errorClassification,
        CancellationToken cancellationToken)
    {
        await invocations.UpdateAsync(invocation, invocation.Version - 1, cancellationToken);
        await auditRecorder.RecordAsync(new AuditRecordRequest(
            invocation.InstallationId,
            invocation.ActorId,
            invocation.CorrelationId,
            invocation.CausationId,
            "tool.invocation-completed",
            outcome,
            new
            {
                InvocationId = invocation.Id.ToString(),
                invocation.RequestHash,
                invocation.ToolDescriptorHash,
            },
            new
            {
                State = invocation.State.ToString(),
                invocation.ExitCode,
                invocation.StandardOutputHash,
                invocation.StandardOutputLength,
                invocation.StandardErrorHash,
                invocation.StandardErrorLength,
                FailureCode = invocation.FailureCode?.ToString(),
            },
            errorClassification), cancellationToken);
        var commit = await unitOfWork.CommitAsync(cancellationToken);
        return commit.Succeeded
            ? DomainResult.Success(invocation)
            : DomainResult.Fail<ToolInvocationRecord>(commit.Failure!);
    }

    private async Task RecordAuthorizedAsync(
        ToolInvocationRecord invocation,
        string policyFingerprint,
        CancellationToken cancellationToken)
    {
        await auditRecorder.RecordAsync(new AuditRecordRequest(
            invocation.InstallationId,
            invocation.ActorId,
            invocation.CorrelationId,
            invocation.CausationId,
            "tool.invocation-authorized",
            AuditOutcome.Succeeded,
            new
            {
                InvocationId = invocation.Id.ToString(),
                invocation.InstallationVersion,
                AgentId = invocation.AgentId.ToString(),
                invocation.AgentVersion,
                invocation.ToolId,
                invocation.ToolVersion,
                invocation.ToolDescriptorHash,
                invocation.CapabilityId,
                RiskClass = invocation.RiskClass.ToString(),
                invocation.ParametersHash,
                TargetKind = invocation.TargetKind.ToString(),
                invocation.TargetHash,
                invocation.WorkspaceHash,
                invocation.RequestHash,
                ApprovalId = invocation.ApprovalId?.ToString(),
                PolicyFingerprint = policyFingerprint,
                invocation.IdempotencyKey,
            },
            new { State = invocation.State.ToString() },
            null), cancellationToken);
    }

    private static DomainResult<ToolInvocationResult> Replay(
        ToolInvocationRecord existing,
        AuthorizationContext context)
    {
        if (!FixedEquals(existing.RequestHash, context.RequestHash) ||
            existing.CorrelationId != context.CorrelationId || existing.CausationId != context.CausationId)
        {
            return DomainResult.Fail<ToolInvocationResult>(new DomainFailure(
                FailureCode.ConcurrencyConflict,
                "Idempotency key is already bound to another tool invocation."));
        }

        if (existing.State is ToolInvocationState.Authorized)
        {
            return DomainResult.Fail<ToolInvocationResult>(new DomainFailure(
                FailureCode.ConcurrencyConflict,
                "An authorized invocation has an uncertain completion and will not be replayed."));
        }

        return DomainResult.Success(new ToolInvocationResult(existing, true, [], [], null));
    }

    private DateTimeOffset TimestampAtOrAfter(ToolInvocationRecord invocation)
    {
        var timestamp = clock.UtcNow;
        return timestamp < invocation.CreatedAt ? invocation.CreatedAt : timestamp;
    }

    private static bool FixedEquals(string first, string second) =>
        first.Length == second.Length && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.ASCII.GetBytes(first),
            System.Text.Encoding.ASCII.GetBytes(second));

    private static bool IsBounded(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength && !value.Any(char.IsControl);

    private static DomainResult<ToolInvocationResult> Invalid(string message) =>
        DomainResult.Fail<ToolInvocationResult>(new DomainFailure(FailureCode.ValidationFailure, message));

    private static DomainResult<ToolInvocationResult> Denied(string message) =>
        DomainResult.Fail<ToolInvocationResult>(new DomainFailure(FailureCode.PolicyDenied, message));

}
