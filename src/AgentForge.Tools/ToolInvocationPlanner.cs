using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using AgentForge.Abstractions.Agents;
using AgentForge.Abstractions.Installations;
using AgentForge.Abstractions.Security;
using AgentForge.Abstractions.Tools;
using AgentForge.Domain.Agents;
using AgentForge.Domain.Installations;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Security;
using AgentForge.Domain.Tools;

namespace AgentForge.Tools;

internal sealed class ToolInvocationPlanner(
    IInstallationRepository installations,
    IAgentIdentityRepository agents,
    IToolCatalog catalog,
    IAuthorizationContextFactory contextFactory,
    ISensitiveDataRedactor redactor) : IToolInvocationPlanner
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public async Task<DomainResult<ToolInvocationPlan>> PlanAsync(
        ToolInvocationPlanRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.ExpectedInstallationVersion < 0 || request.AgentId.Value == Guid.Empty ||
            request.AgentVersion < 0 || !IsBounded(request.ActorId.Value, 256) ||
            !IsBounded(request.CorrelationId.Value, 128) ||
            request.CausationId is { } causation && !IsBounded(causation.Value, 128) ||
            !IsBounded(request.Workspace, 2048) || request.Parameters is null || request.Parameters.Count > 64 ||
            request.Parameters.Keys.Any(item => !IsBounded(item, 128)))
        {
            return InvalidPlan("Tool invocation identity, workspace, or parameters are invalid.");
        }

        var parameters = new ReadOnlyDictionary<string, ToolParameterValue>(
            new Dictionary<string, ToolParameterValue>(request.Parameters, StringComparer.Ordinal));
        if (redactor.Redact(new
        {
            ActorId = request.ActorId.Value,
            CorrelationId = request.CorrelationId.Value,
            CausationId = request.CausationId?.Value,
            request.Workspace,
            Parameters = parameters,
        }).ContainsRedactions)
        {
            return InvalidPlan("Tool invocation accepts no direct credential-shaped input; use secret references.");
        }

        var installation = await installations.ReadAsync(cancellationToken);
        if (installation.State is not InstallationState.Ready ||
            installation.Version != request.ExpectedInstallationVersion)
        {
            return DeniedPlan("Tool invocation requires the exact current Ready installation version.");
        }

        var agent = await agents.FindByIdAsync(request.AgentId, cancellationToken);
        if (agent is null || agent.InstallationId != installation.Id || agent.Version != request.AgentVersion)
        {
            return DeniedPlan("Tool invocation requires the exact current agent policy version.");
        }

        var described = await catalog.DescribeAsync(request.ToolId, request.ToolVersion, cancellationToken);
        if (!described.IsSuccess)
        {
            return DomainResult.Fail<ToolInvocationPlan>(described.Failure!);
        }

        var descriptor = described.Value;
        if (!NetworkPolicyAllows(agent.CapabilityPolicy.NetworkPosture, descriptor.Definition.Process.NetworkPolicy))
        {
            return DeniedPlan("The agent network posture does not permit the descriptor's network policy.");
        }

        var prepared = Prepare(descriptor.Definition, parameters);
        if (!prepared.IsSuccess)
        {
            return DomainResult.Fail<ToolInvocationPlan>(prepared.Failure!);
        }

        var invocation = new CapabilityInvocationRequest(
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
            request.CausationId);
        var authorization = contextFactory.Create(invocation);
        if (!authorization.IsSuccess)
        {
            return DomainResult.Fail<ToolInvocationPlan>(authorization.Failure!);
        }

        if (descriptor.Definition.ExecutionKind is ToolExecutionKind.BuiltIn)
        {
            var contained = descriptor.Definition.BuiltInHandlerId switch
            {
                "workspace.list" => WorkspacePathGuard.ResolveTarget(
                    authorization.Value.NormalizedWorkspace!, authorization.Value.NormalizedTarget!, requireDirectory: true),
                "workspace.read-text" => WorkspacePathGuard.ResolveTarget(
                    authorization.Value.NormalizedWorkspace!, authorization.Value.NormalizedTarget!, requireDirectory: false),
                "search.brave" => DomainResult.Success(new ContainedWorkspaceTarget(
                    authorization.Value.NormalizedWorkspace!, authorization.Value.NormalizedTarget!)),
                "http-api.get" => DomainResult.Success(new ContainedWorkspaceTarget(
                    authorization.Value.NormalizedWorkspace!, authorization.Value.NormalizedTarget!)),
                _ => DomainResult.Fail<ContainedWorkspaceTarget>(new DomainFailure(
                    FailureCode.UnsupportedCapability,
                    "The built-in tool handler is not available.")),
            };
            if (!contained.IsSuccess)
            {
                return DomainResult.Fail<ToolInvocationPlan>(contained.Failure!);
            }
        }

        return DomainResult.Success(new ToolInvocationPlan(
            invocation,
            authorization.Value,
            descriptor,
            prepared.Value.Arguments));
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
                return DomainResult.Success(new NormalizedValue(number, number.ToString(CultureInfo.InvariantCulture)));
            case (ToolParameterType.Switch, ToolParameterValueKind.Switch)
                when value.Text is null && value.WholeNumber is null && value.Switch is { } enabled:
                return DomainResult.Success(new NormalizedValue(enabled, enabled ? bool.TrueString : bool.FalseString));
            default:
                return InvalidValue("Tool parameter value does not match its exact type and bounds.");
        }
    }

    private static bool NetworkPolicyAllows(NetworkPosture posture, ProcessNetworkPolicy policy) => posture switch
    {
        NetworkPosture.Denied => policy is ProcessNetworkPolicy.Denied,
        NetworkPosture.LoopbackOnly => policy is ProcessNetworkPolicy.Denied or ProcessNetworkPolicy.LoopbackOnly,
        NetworkPosture.ApprovedEndpointsOnly => policy is ProcessNetworkPolicy.Denied or
            ProcessNetworkPolicy.LoopbackOnly or ProcessNetworkPolicy.FixedEndpointOnly,
        _ => false,
    };

    private static bool IsBounded(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength && !value.Any(char.IsControl);

    private static DomainResult<ToolInvocationPlan> InvalidPlan(string message) =>
        DomainResult.Fail<ToolInvocationPlan>(new DomainFailure(FailureCode.ValidationFailure, message));

    private static DomainResult<ToolInvocationPlan> DeniedPlan(string message) =>
        DomainResult.Fail<ToolInvocationPlan>(new DomainFailure(FailureCode.PolicyDenied, message));

    private static DomainResult<PreparedInvocation> InvalidPrepared(string message) =>
        DomainResult.Fail<PreparedInvocation>(new DomainFailure(FailureCode.ValidationFailure, message));

    private static DomainResult<NormalizedValue> InvalidValue(string message) =>
        DomainResult.Fail<NormalizedValue>(new DomainFailure(FailureCode.ValidationFailure, message));

    private sealed record PreparedInvocation(string ParametersJson, string? Target, IReadOnlyList<string> Arguments);
    private sealed record NormalizedValue(object CanonicalValue, string ArgumentValue);
}
