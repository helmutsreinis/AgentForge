using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
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
    IToolCatalog catalog,
    IAuthorizationContextFactory contextFactory,
    ICapabilityPolicyFactory policyFactory,
    ICapabilityPolicyEvaluator policyEvaluator,
    ISensitiveDataRedactor redactor,
    IAuditRecorder auditRecorder,
    IUnitOfWork unitOfWork,
    IClock clock,
    IIdentifierGenerator identifiers,
    ISandbox sandbox) : IToolInvocationService
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public async Task<DomainResult<ToolInvocationResult>> InvokeAsync(
        ToolInvocationRequest request,
        IProcessOutputObserver? observer,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.ExpectedInstallationVersion < 0 || request.AgentId.Value == Guid.Empty ||
            request.AgentVersion < 0 || !IsBounded(request.ActorId.Value, 256) ||
            !IsBounded(request.IdempotencyKey, 256) || !IsBounded(request.CorrelationId.Value, 128) ||
            request.CausationId is { } causation && !IsBounded(causation.Value, 128) ||
            !IsBounded(request.Workspace, 2048) || request.Parameters is null || request.Parameters.Count > 64 ||
            request.Parameters.Keys.Any(item => !IsBounded(item, 128)))
        {
            return Invalid("Tool invocation identity, idempotency, or parameters are invalid.");
        }

        request = request with
        {
            Parameters = new ReadOnlyDictionary<string, ToolParameterValue>(
                new Dictionary<string, ToolParameterValue>(request.Parameters, StringComparer.Ordinal)),
        };
        if (redactor.Redact(new
        {
            ActorId = request.ActorId.Value,
            request.IdempotencyKey,
            CorrelationId = request.CorrelationId.Value,
            CausationId = request.CausationId?.Value,
            request.Workspace,
            request.Parameters,
        }).ContainsRedactions)
        {
            return Invalid("Tool invocation accepts no direct credential-shaped input; use secret references.");
        }

        var installation = await installations.ReadAsync(cancellationToken);
        if (installation.State is not InstallationState.Ready ||
            installation.Version != request.ExpectedInstallationVersion)
        {
            return Denied("Tool invocation requires the exact current Ready installation version.");
        }

        var agent = await agents.FindByIdAsync(request.AgentId, cancellationToken);
        if (agent is null || agent.InstallationId != installation.Id || agent.Version != request.AgentVersion)
        {
            return Denied("Tool invocation requires the exact current agent policy version.");
        }

        var described = await catalog.DescribeAsync(request.ToolId, request.ToolVersion, cancellationToken);
        if (!described.IsSuccess)
        {
            return DomainResult.Fail<ToolInvocationResult>(described.Failure!);
        }

        var descriptor = described.Value;
        if (!NetworkPolicyAllows(agent.CapabilityPolicy.NetworkPosture, descriptor.Definition.Process.NetworkPolicy))
        {
            return Denied("The agent network posture does not permit the descriptor's process network policy.");
        }

        var prepared = Prepare(descriptor.Definition, request.Parameters);
        if (!prepared.IsSuccess)
        {
            return DomainResult.Fail<ToolInvocationResult>(prepared.Failure!);
        }

        var authorization = contextFactory.Create(new CapabilityInvocationRequest(
            installation.Id,
            installation.Version,
            agent.Id,
            agent.Version,
            request.ActorId,
            descriptor.Definition.CapabilityId,
            descriptor.Definition.RiskClass,
            descriptor.Definition.Id,
            descriptor.Definition.Version,
            descriptor.DescriptorHash,
            prepared.Value.ParametersJson,
            descriptor.Definition.TargetKind,
            prepared.Value.Target,
            request.Workspace,
            request.CorrelationId,
            request.CausationId));
        if (!authorization.IsSuccess)
        {
            return DomainResult.Fail<ToolInvocationResult>(authorization.Failure!);
        }

        var existing = await invocations.FindByIdempotencyKeyAsync(
            installation.Id,
            request.IdempotencyKey,
            cancellationToken);
        if (existing is not null)
        {
            return Replay(existing, authorization.Value);
        }

        var approval = await approvals.FindLatestAsync(
            installation.Id,
            agent.Id,
            authorization.Value.RequestHash,
            cancellationToken);
        var policy = policyFactory.Create(agent, authorization.Value);
        var evaluation = policyEvaluator.Evaluate(authorization.Value, policy, approval, clock.UtcNow);
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
                authorization.Value.RequestHash,
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
            authorization.Value,
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
            policyFactory.Create(currentAgent, authorization.Value).Fingerprint);
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

        var processRequest = new ProcessExecutionRequest(
            descriptor.Definition.Process.ExecutablePath,
            prepared.Value.Arguments,
            authorization.Value.NormalizedWorkspace!,
            authorization.Value.NormalizedWorkspace!,
            new Dictionary<string, string>(StringComparer.Ordinal),
            TimeSpan.FromSeconds(descriptor.Definition.Process.TimeoutSeconds),
            descriptor.Definition.Process.MaximumOutputBytes,
            descriptor.Definition.Process.NetworkPolicy,
            descriptor.Definition.Process.RequiredSandbox,
            descriptor.Definition.Process.RequiredFeatures,
            descriptor.Definition.SideEffects.HasFlag(ToolSideEffectKind.WritesFileSystem)
                ? ProcessFileSystemPolicy.ReadWriteWorkspace
                : ProcessFileSystemPolicy.ReadOnlyWorkspace);
        try
        {
            var execution = await sandbox.ExecuteAsync(processRequest, observer, cancellationToken);
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

    private static DomainResult<PreparedInvocation> Prepare(
        ToolDescriptorDefinition descriptor,
        IReadOnlyDictionary<string, ToolParameterValue> supplied)
    {
        var declared = descriptor.Parameters.ToDictionary(item => item.Name, StringComparer.Ordinal);
        if (supplied.Keys.Any(item => !declared.ContainsKey(item)) ||
            descriptor.Parameters.Any(item => item.Required && !supplied.ContainsKey(item.Name)))
        {
            return InvalidPrepared("Tool values must match the exact descriptor parameter schema.");
        }

        var normalized = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        var rendered = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in supplied)
        {
            var value = NormalizeValue(declared[pair.Key], pair.Value);
            if (!value.IsSuccess)
            {
                return DomainResult.Fail<PreparedInvocation>(value.Failure!);
            }

            normalized.Add(pair.Key, value.Value.CanonicalValue);
            rendered.Add(pair.Key, value.Value.ArgumentValue);
        }

        var arguments = new List<string>(descriptor.Process.FixedArguments);
        foreach (var binding in descriptor.Process.ArgumentBindings)
        {
            if (binding.Kind is ToolArgumentBindingKind.Literal)
            {
                arguments.Add(binding.Token!);
                continue;
            }

            if (!supplied.TryGetValue(binding.ParameterName!, out var suppliedValue))
            {
                continue;
            }

            switch (binding.Kind)
            {
                case ToolArgumentBindingKind.Positional:
                    arguments.Add(rendered[binding.ParameterName!]);
                    break;
                case ToolArgumentBindingKind.NamedValue:
                    arguments.Add(binding.Token!);
                    arguments.Add(rendered[binding.ParameterName!]);
                    break;
                case ToolArgumentBindingKind.BooleanSwitch when suppliedValue.Switch is true:
                    arguments.Add(binding.Token!);
                    break;
            }
        }

        var target = descriptor.TargetParameterName is null
            ? null
            : (string?)normalized[descriptor.TargetParameterName];
        return DomainResult.Success(new PreparedInvocation(
            JsonSerializer.Serialize(normalized, SerializerOptions),
            target,
            Array.AsReadOnly(arguments.ToArray())));
    }

    private static DomainResult<NormalizedValue> NormalizeValue(
        ToolParameterDescriptor descriptor,
        ToolParameterValue value)
    {
        if (value is null || !Enum.IsDefined(value.Kind))
        {
            return InvalidValue("Tool parameter value kind is invalid.");
        }

        switch (descriptor.Type, value.Kind)
        {
            case (ToolParameterType.Text, ToolParameterValueKind.Text)
                when value.Text is not null && value.WholeNumber is null && value.Switch is null &&
                value.Text.Length <= descriptor.MaximumLength && !value.Text.Any(char.IsControl) &&
                (descriptor.AllowedValues.Count == 0 || descriptor.AllowedValues.Contains(value.Text, StringComparer.Ordinal)):
                return DomainResult.Success(new NormalizedValue(value.Text, value.Text));
            case (ToolParameterType.WholeNumber, ToolParameterValueKind.WholeNumber)
                when value.Text is null && value.WholeNumber is { } number && value.Switch is null &&
                number >= descriptor.MinimumInteger && number <= descriptor.MaximumInteger:
                return DomainResult.Success(new NormalizedValue(
                    number,
                    number.ToString(CultureInfo.InvariantCulture)));
            case (ToolParameterType.Switch, ToolParameterValueKind.Switch)
                when value.Text is null && value.WholeNumber is null && value.Switch is { } enabled:
                return DomainResult.Success(new NormalizedValue(
                    enabled,
                    enabled ? bool.TrueString : bool.FalseString));
            default:
                return InvalidValue("Tool parameter value does not match its exact type and bounds.");
        }
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

    private static bool NetworkPolicyAllows(NetworkPosture posture, ProcessNetworkPolicy policy) =>
        posture switch
        {
            NetworkPosture.Denied => policy is ProcessNetworkPolicy.Denied,
            NetworkPosture.LoopbackOnly => policy is ProcessNetworkPolicy.Denied or ProcessNetworkPolicy.LoopbackOnly,
            _ => false,
        };

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

    private static DomainResult<PreparedInvocation> InvalidPrepared(string message) =>
        DomainResult.Fail<PreparedInvocation>(new DomainFailure(FailureCode.ValidationFailure, message));

    private static DomainResult<NormalizedValue> InvalidValue(string message) =>
        DomainResult.Fail<NormalizedValue>(new DomainFailure(FailureCode.ValidationFailure, message));

    private sealed record PreparedInvocation(
        string ParametersJson,
        string? Target,
        IReadOnlyList<string> Arguments);

    private sealed record NormalizedValue(object CanonicalValue, string ArgumentValue);
}
