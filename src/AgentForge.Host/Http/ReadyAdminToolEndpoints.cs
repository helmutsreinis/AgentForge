using System.Text;
using System.Text.Json;
using AgentForge.Abstractions.Agents;
using AgentForge.Abstractions.Installations;
using AgentForge.Abstractions.Security;
using AgentForge.Abstractions.Setup;
using AgentForge.Abstractions.Time;
using AgentForge.Abstractions.Tools;
using AgentForge.Domain.Agents;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Security;
using AgentForge.Domain.Setup;
using AgentForge.Domain.Tools;

namespace AgentForge.Host.Http;

internal sealed record PreviewAgentToolGrantWebRequest(
    long ExpectedInstallationVersion,
    long ExpectedAgentVersion,
    string CapabilityId,
    bool Grant,
    int MaximumToolInvocations = 10);

internal sealed record PreviewToolInvocationWebRequest(
    long ExpectedInstallationVersion,
    long ExpectedAgentVersion,
    string ToolId,
    string ToolVersion,
    string Workspace,
    IReadOnlyDictionary<string, JsonElement> Parameters,
    string Disposition = "grant",
    int ApprovalSeconds = 300);

internal static partial class ReadyAdminEndpoints
{
    private static async Task<IResult> ListToolsAsync(
        HttpContext context,
        ReadyAdminSessionManager sessions,
        IInstallationStateReader stateReader,
        IAgentIdentityRepository agents,
        IToolCatalog catalog,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var acquired = await AcquireAsync(
            context, sessions, stateReader, clock, requireCsrf: false, cancellationToken);
        if (acquired.Failure is not null)
        {
            return acquired.Failure;
        }

        var summaries = await catalog.SearchAsync(
            new ToolSearchRequest(string.Empty, null, null, 50), cancellationToken);
        if (!summaries.IsSuccess)
        {
            return DomainProblem(context, summaries.Failure!, "Tool catalog unavailable");
        }

        var descriptors = new List<ToolDescriptor>();
        foreach (var summary in summaries.Value)
        {
            var described = await catalog.DescribeAsync(summary.Id, summary.Version, cancellationToken);
            if (!described.IsSuccess)
            {
                return DomainProblem(context, described.Failure!, "Tool catalog changed");
            }

            descriptors.Add(described.Value);
        }

        var installationAgents = await agents.ListAsync(acquired.Session!.InstallationId, cancellationToken);
        var installation = await stateReader.ReadAsync(cancellationToken);
        return Results.Ok(new
        {
            installationVersion = installation.Version,
            tools = descriptors.Select(item => new
            {
                id = item.Definition.Id,
                version = item.Definition.Version,
                name = item.Definition.Name,
                summary = item.Definition.Summary,
                description = item.Definition.Description,
                capabilityId = item.Definition.CapabilityId,
                riskClass = item.Definition.RiskClass.ToString(),
                targetKind = item.Definition.TargetKind.ToString(),
                targetParameterName = item.Definition.TargetParameterName,
                sideEffects = item.Definition.SideEffects.ToString(),
                outputSensitivity = item.Definition.OutputSensitivity.ToString(),
                executionKind = item.Definition.ExecutionKind.ToString(),
                networkPolicy = item.Definition.Process.NetworkPolicy.ToString(),
                sandbox = item.Definition.Process.RequiredSandbox.ToString(),
                maximumOutputBytes = item.Definition.Process.MaximumOutputBytes,
                descriptorHash = item.DescriptorHash,
                parameters = item.Definition.Parameters.Select(parameter => new
                {
                    parameter.Name,
                    type = parameter.Type.ToString(),
                    parameter.Required,
                    parameter.MaximumLength,
                    parameter.MinimumInteger,
                    parameter.MaximumInteger,
                    parameter.AllowedValues,
                    parameter.Description,
                }),
            }),
            agents = installationAgents.Select(agent => new
            {
                id = agent.Id.Value,
                agent.Name,
                agent.Version,
                agent.DefaultWorkspace,
                toolGrants = agent.CapabilityPolicy.ToolGrants,
                agent.Budget.MaxToolInvocations,
            }),
            correlationId = context.TraceIdentifier,
        });
    }

    private static async Task<IResult> PreviewAgentToolGrantAsync(
        Guid agentId,
        PreviewAgentToolGrantWebRequest request,
        HttpContext context,
        ReadyAdminSessionManager sessions,
        IInstallationStateReader stateReader,
        IAgentIdentityRepository agents,
        IToolCatalog catalog,
        ILocalAdministratorRepository administrators,
        ISecretStore secretStore,
        ISetupProfileEditor editor,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var acquired = await AcquireMutationAsync(context, sessions, stateReader, clock, cancellationToken);
        if (acquired.Failure is not null)
        {
            return acquired.Failure;
        }

        var capabilityId = (request.CapabilityId ?? string.Empty).Trim();
        if (agentId == Guid.Empty || !Text(capabilityId, 256) ||
            request.Grant && request.MaximumToolInvocations is < 1 or > 1000)
        {
            return Problem(context, 400, "Invalid tool grant",
                "Select an exact tool capability and a per-run ceiling from 1 to 1000.", "validation-failure");
        }

        var available = await catalog.SearchAsync(
            new ToolSearchRequest(string.Empty, capabilityId, null, 50), cancellationToken);
        if (!available.IsSuccess || available.Value.Count == 0)
        {
            return Problem(context, 404, "Tool capability unavailable",
                "No authoritative descriptor exposes the selected capability.", "not-found");
        }

        var session = acquired.Session!;
        var key = context.Request.Headers["Idempotency-Key"].ToString();
        var scopedKey = $"tool-grant-preview:{agentId:D}:{capabilityId}:{request.Grant}:{key}";
        var requestHash = SnapshotHash(new { agentId, request, capabilityId });
        await session.MutationGate.WaitAsync(cancellationToken);
        try
        {
            if (session.Results.TryGetValue(scopedKey, out var existing))
            {
                return string.Equals(existing.RequestHash, requestHash, StringComparison.Ordinal)
                    ? Replay(context, existing.Response)
                    : Problem(context, 409, "Idempotency conflict",
                        "The idempotency key is already bound to another tool grant preview.", "idempotency-conflict");
            }

            var agent = await agents.FindByIdAsync(new AgentIdentityId(agentId), cancellationToken);
            if (agent is null || agent.InstallationId != session.InstallationId)
            {
                return Problem(context, 404, "Agent not found",
                    "The selected agent does not belong to this installation.", "not-found");
            }

            var currentlyGranted = agent.CapabilityPolicy.ToolGrants.Contains(capabilityId, StringComparer.Ordinal);
            if (currentlyGranted == request.Grant)
            {
                return Problem(context, 400, "No grant change",
                    request.Grant ? "The agent already has this tool capability." : "The agent does not have this tool capability.",
                    "validation-failure");
            }

            var grants = request.Grant
                ? agent.CapabilityPolicy.ToolGrants.Append(capabilityId).Order(StringComparer.Ordinal).ToArray()
                : agent.CapabilityPolicy.ToolGrants.Where(item => !string.Equals(item, capabilityId, StringComparison.Ordinal)).ToArray();
            var maximumInvocations = request.Grant
                ? request.MaximumToolInvocations
                : grants.Length == 0 ? 0 : agent.Budget.MaxToolInvocations;
            var candidate = AgentCandidate(agent) with
            {
                CapabilityPolicy = agent.CapabilityPolicy with { ToolGrants = grants },
                Budget = agent.Budget with { MaxToolInvocations = maximumInvocations },
            };
            var credential = await MaterializeAdministratorCredentialAsync(
                session.InstallationId, administrators, secretStore, cancellationToken);
            if (!credential.IsSuccess)
            {
                return DomainProblem(context, credential.Failure!, "Tool grant preview failed");
            }

            var stable = StableRequestIdentity(session.InstallationId, "tool-grant-preview", key);
            var correlation = new CorrelationId($"tool-grant:{Convert.ToHexStringLower(stable.AsSpan(0, 16))}");
            DomainResult<AgentEditPreview> preview;
            await using (var lease = credential.Value)
            {
                preview = await editor.PreviewAgentAsync(new PreviewAgentEditRequest(
                    agent.Id,
                    request.ExpectedInstallationVersion,
                    request.ExpectedAgentVersion,
                    candidate,
                    session.ActorId,
                    correlation,
                    lease.Value), cancellationToken);
            }
            if (!preview.IsSuccess)
            {
                return DomainProblem(context, preview.Failure!, "Tool grant preview failed");
            }

            RetainBoundedPreviews(session.ToolGrantPreviews, 7);
            session.ToolGrantPreviews[preview.Value.RequestHash] = new ReadyAgentToolGrantPreview(
                agent.Id,
                request.ExpectedInstallationVersion,
                request.ExpectedAgentVersion,
                capabilityId,
                request.Grant,
                candidate,
                correlation,
                preview.Value.RequestHash);
            var response = new
            {
                previewHash = preview.Value.RequestHash,
                action = request.Grant ? "grant" : "revoke",
                capabilityId,
                descriptors = available.Value.Select(item => new { item.Id, item.Version, item.DescriptorHash }),
                maximumToolInvocations = maximumInvocations,
                changes = preview.Value.Changes.Select(ChangeResponse),
                warning = "This grants catalog visibility only. Every exact invocation still requires a separately reviewed, expiring, single-use approval.",
                correlationId = correlation.Value,
            };
            StoreIdempotentResult(session, scopedKey, requestHash, response);
            return Results.Ok(response);
        }
        finally
        {
            session.MutationGate.Release();
        }
    }

    private static async Task<IResult> ApplyAgentToolGrantAsync(
        Guid agentId,
        ReadyEditApplyWebRequest request,
        HttpContext context,
        ReadyAdminSessionManager sessions,
        IInstallationStateReader stateReader,
        ILocalAdministratorRepository administrators,
        ISecretStore secretStore,
        ISetupProfileEditor editor,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var acquired = await AcquireMutationAsync(context, sessions, stateReader, clock, cancellationToken);
        if (acquired.Failure is not null)
        {
            return acquired.Failure;
        }

        var session = acquired.Session!;
        var key = context.Request.Headers["Idempotency-Key"].ToString();
        var scopedKey = $"tool-grant-apply:{agentId:D}:{key}";
        var requestHash = SnapshotHash(new { agentId, request.PreviewHash });
        await session.MutationGate.WaitAsync(cancellationToken);
        try
        {
            if (session.Results.TryGetValue(scopedKey, out var existing))
            {
                return string.Equals(existing.RequestHash, requestHash, StringComparison.Ordinal)
                    ? Replay(context, existing.Response)
                    : Problem(context, 409, "Idempotency conflict",
                        "The idempotency key is already bound to another tool grant.", "idempotency-conflict");
            }
            if (!Text(request.PreviewHash, 128) ||
                !session.ToolGrantPreviews.TryGetValue(request.PreviewHash, out var approved) ||
                approved.AgentId != new AgentIdentityId(agentId))
            {
                return Problem(context, 403, "Approved preview required",
                    "Preview the exact tool authority and budget change in this operator session before applying it.",
                    "policy-denied");
            }

            var credential = await MaterializeAdministratorCredentialAsync(
                session.InstallationId, administrators, secretStore, cancellationToken);
            if (!credential.IsSuccess)
            {
                return DomainProblem(context, credential.Failure!, "Tool grant failed");
            }

            DomainResult<AgentEditResult> applied;
            await using (var lease = credential.Value)
            {
                applied = await editor.ApplyAgentAsync(new ApplyAgentEditRequest(
                    approved.AgentId,
                    approved.ExpectedInstallationVersion,
                    approved.ExpectedAgentVersion,
                    approved.Candidate,
                    approved.RequestHash,
                    session.ActorId,
                    approved.CorrelationId,
                    lease.Value), cancellationToken);
            }
            if (!applied.IsSuccess)
            {
                return DomainProblem(context, applied.Failure!, "Tool grant failed");
            }

            var response = new
            {
                installationVersion = applied.Value.Installation.Version,
                agentId = applied.Value.Agent.Id.Value,
                agentVersion = applied.Value.Agent.Version,
                approved.CapabilityId,
                granted = approved.Grant,
                maximumToolInvocations = applied.Value.Agent.Budget.MaxToolInvocations,
                changes = applied.Value.Changes.Select(ChangeResponse),
                previewHash = applied.Value.RequestHash,
                correlationId = approved.CorrelationId.Value,
            };
            session.ToolGrantPreviews.TryRemove(request.PreviewHash, out _);
            StoreIdempotentResult(session, scopedKey, requestHash, response);
            return Results.Ok(response);
        }
        finally
        {
            session.MutationGate.Release();
        }
    }

    private static async Task<IResult> PreviewToolInvocationAsync(
        Guid agentId,
        PreviewToolInvocationWebRequest request,
        HttpContext context,
        ReadyAdminSessionManager sessions,
        IInstallationStateReader stateReader,
        IToolCatalog catalog,
        IToolInvocationPlanner planner,
        ICapabilityApprovalService approvalService,
        ILocalAdministratorRepository administrators,
        ISecretStore secretStore,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var acquired = await AcquireMutationAsync(context, sessions, stateReader, clock, cancellationToken);
        if (acquired.Failure is not null)
        {
            return acquired.Failure;
        }

        var dispositionText = (request.Disposition ?? string.Empty).Trim();
        if (!string.Equals(dispositionText, "grant", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(dispositionText, "deny", StringComparison.OrdinalIgnoreCase))
        {
            return Problem(context, 400, "Invalid approval disposition",
                "Choose an exact grant or denial preview.", "validation-failure");
        }
        var disposition = string.Equals(dispositionText, "deny", StringComparison.OrdinalIgnoreCase)
            ? CapabilityApprovalDisposition.Deny
            : CapabilityApprovalDisposition.Grant;
        if (agentId == Guid.Empty || request.ApprovalSeconds is < 30 or > 600 ||
            !Text(request.ToolId, 256) || !Text(request.ToolVersion, 128) ||
            !Text(request.Workspace, 2048) || request.Parameters is null || request.Parameters.Count > 64)
        {
            return Problem(context, 400, "Invalid tool request",
                "Tool approval requires exact bounded identity, workspace, parameters, and a 30–600 second lifetime.",
                "validation-failure");
        }

        var described = await catalog.DescribeAsync(request.ToolId, request.ToolVersion, cancellationToken);
        if (!described.IsSuccess)
        {
            return DomainProblem(context, described.Failure!, "Tool unavailable");
        }

        var parameters = ConvertParameters(described.Value, request.Parameters);
        if (!parameters.IsSuccess)
        {
            return DomainProblem(context, parameters.Failure!, "Tool parameters invalid");
        }

        var session = acquired.Session!;
        var key = context.Request.Headers["Idempotency-Key"].ToString();
        var scopedKey = $"tool-invocation-preview:{agentId:D}:{key}";
        var requestHash = SnapshotHash(new { agentId, request, disposition });
        await session.MutationGate.WaitAsync(cancellationToken);
        try
        {
            if (session.Results.TryGetValue(scopedKey, out var existing))
            {
                return string.Equals(existing.RequestHash, requestHash, StringComparison.Ordinal)
                    ? Replay(context, existing.Response)
                    : Problem(context, 409, "Idempotency conflict",
                        "The idempotency key is already bound to another tool invocation preview.", "idempotency-conflict");
            }

            var stable = StableRequestIdentity(session.InstallationId, "tool-invocation-preview", key);
            var correlation = new CorrelationId($"tool-invocation:{Convert.ToHexStringLower(stable.AsSpan(0, 16))}");
            var planned = await planner.PlanAsync(new ToolInvocationPlanRequest(
                request.ExpectedInstallationVersion,
                new AgentIdentityId(agentId),
                request.ExpectedAgentVersion,
                session.ActorId,
                request.ToolId,
                request.ToolVersion,
                parameters.Value,
                request.Workspace,
                correlation), cancellationToken);
            if (!planned.IsSuccess)
            {
                return DomainProblem(context, planned.Failure!, "Tool invocation denied");
            }

            var credential = await MaterializeAdministratorCredentialAsync(
                session.InstallationId, administrators, secretStore, cancellationToken);
            if (!credential.IsSuccess)
            {
                return DomainProblem(context, credential.Failure!, "Tool approval preview failed");
            }

            var expiresAt = clock.UtcNow.AddSeconds(request.ApprovalSeconds);
            DomainResult<CapabilityApprovalPreview> preview;
            await using (var lease = credential.Value)
            {
                preview = await approvalService.PreviewAsync(new PreviewCapabilityApprovalRequest(
                    planned.Value.Invocation,
                    disposition,
                    expiresAt,
                    session.ActorId,
                    correlation,
                    lease.Value), cancellationToken);
            }
            if (!preview.IsSuccess)
            {
                return DomainProblem(context, preview.Failure!, "Tool approval preview failed");
            }

            RetainBoundedPreviews(session.ToolInvocationPreviews, 7);
            session.ToolInvocationPreviews[preview.Value.PreviewHash] = new ReadyToolInvocationPreview(
                planned.Value,
                parameters.Value,
                disposition,
                expiresAt,
                correlation,
                preview.Value.PreviewHash);
            var definition = planned.Value.Descriptor.Definition;
            var response = new
            {
                previewHash = preview.Value.PreviewHash,
                requestHash = preview.Value.RequestHash,
                disposition = disposition.ToString(),
                expiresAt,
                tool = new
                {
                    id = definition.Id,
                    version = definition.Version,
                    definition.Name,
                    capabilityId = definition.CapabilityId,
                    riskClass = definition.RiskClass.ToString(),
                    descriptorHash = planned.Value.Descriptor.DescriptorHash,
                    sideEffects = definition.SideEffects.ToString(),
                    sandbox = definition.Process.RequiredSandbox.ToString(),
                    networkPolicy = definition.Process.NetworkPolicy.ToString(),
                    maximumOutputBytes = definition.Process.MaximumOutputBytes,
                },
                parametersJson = preview.Value.Parameters.Json,
                targetJson = preview.Value.Target.Json,
                workspaceJson = preview.Value.Workspace.Json,
                policy = new
                {
                    decision = preview.Value.PolicyEvaluation.Decision.ToString(),
                    preview.Value.PolicyEvaluation.Reason,
                },
                warning = disposition is CapabilityApprovalDisposition.Grant
                    ? "Grant is single-use and consumed before execution. Any version, hash, parameter, target, workspace, or policy change invalidates it."
                    : "Deny records an exact expiring rejection for this request and performs no tool execution.",
                correlationId = correlation.Value,
            };
            StoreIdempotentResult(session, scopedKey, requestHash, response);
            return Results.Ok(response);
        }
        finally
        {
            session.MutationGate.Release();
        }
    }

    private static async Task<IResult> ApplyToolInvocationAsync(
        Guid agentId,
        ReadyEditApplyWebRequest request,
        HttpContext context,
        ReadyAdminSessionManager sessions,
        IInstallationStateReader stateReader,
        ICapabilityApprovalService approvalService,
        IToolInvocationService invocationService,
        ILocalAdministratorRepository administrators,
        ISecretStore secretStore,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var acquired = await AcquireMutationAsync(context, sessions, stateReader, clock, cancellationToken);
        if (acquired.Failure is not null)
        {
            return acquired.Failure;
        }

        var session = acquired.Session!;
        var key = context.Request.Headers["Idempotency-Key"].ToString();
        var scopedKey = $"tool-invocation-apply:{agentId:D}:{key}";
        var requestHash = SnapshotHash(new { agentId, request.PreviewHash });
        await session.MutationGate.WaitAsync(cancellationToken);
        try
        {
            if (session.Results.TryGetValue(scopedKey, out var existing))
            {
                return string.Equals(existing.RequestHash, requestHash, StringComparison.Ordinal)
                    ? Replay(context, existing.Response)
                    : Problem(context, 409, "Idempotency conflict",
                        "The idempotency key is already bound to another exact tool request.", "idempotency-conflict");
            }
            if (!Text(request.PreviewHash, 128) ||
                !session.ToolInvocationPreviews.TryGetValue(request.PreviewHash, out var approved) ||
                approved.Plan.Authorization.AgentId != new AgentIdentityId(agentId))
            {
                return Problem(context, 403, "Approved preview required",
                    "Review this exact tool request in the current operator session before applying it.", "policy-denied");
            }
            if (approved.ExpiresAt <= clock.UtcNow)
            {
                session.ToolInvocationPreviews.TryRemove(request.PreviewHash, out _);
                return Problem(context, 409, "Approval preview expired",
                    "Create a new exact preview before running the tool.", "approval-expired");
            }

            var credential = await MaterializeAdministratorCredentialAsync(
                session.InstallationId, administrators, secretStore, cancellationToken);
            if (!credential.IsSuccess)
            {
                return DomainProblem(context, credential.Failure!, "Tool approval failed");
            }

            DomainResult<CapabilityApproval> approval;
            await using (var lease = credential.Value)
            {
                approval = await approvalService.ApplyAsync(new ApplyCapabilityApprovalRequest(
                    approved.Plan.Invocation,
                    approved.Disposition,
                    approved.ExpiresAt,
                    approved.PreviewHash,
                    $"tool-approval:{key}",
                    session.ActorId,
                    approved.CorrelationId,
                    lease.Value), cancellationToken);
            }
            if (!approval.IsSuccess)
            {
                return DomainProblem(context, approval.Failure!, "Tool approval failed");
            }

            object response;
            if (approved.Disposition is CapabilityApprovalDisposition.Deny)
            {
                response = new
                {
                    approvalId = approval.Value.Id.Value,
                    disposition = "Denied",
                    executed = false,
                    expiresAt = approval.Value.ExpiresAt,
                    requestHash = approval.Value.RequestHash,
                    correlationId = approved.CorrelationId.Value,
                };
            }
            else
            {
                var invocation = await invocationService.InvokeAsync(new ToolInvocationRequest(
                    approved.Plan.Invocation.InstallationVersion,
                    approved.Plan.Invocation.AgentId,
                    approved.Plan.Invocation.AgentVersion,
                    session.ActorId,
                    approved.Plan.Invocation.ToolId!,
                    approved.Plan.Invocation.ToolVersion!,
                    approved.Parameters,
                    approved.Plan.Authorization.NormalizedWorkspace!,
                    $"tool-invocation:{key}",
                    approved.CorrelationId), null, cancellationToken);
                if (!invocation.IsSuccess)
                {
                    return DomainProblem(context, invocation.Failure!, "Tool execution failed");
                }

                response = new
                {
                    approvalId = approval.Value.Id.Value,
                    invocationId = invocation.Value.Invocation.Id.Value,
                    state = invocation.Value.Invocation.State.ToString(),
                    exitCode = invocation.Value.Invocation.ExitCode,
                    output = Encoding.UTF8.GetString(invocation.Value.StandardOutput),
                    standardError = Encoding.UTF8.GetString(invocation.Value.StandardError),
                    outputHash = invocation.Value.Invocation.StandardOutputHash,
                    outputLength = invocation.Value.Invocation.StandardOutputLength,
                    sandbox = invocation.Value.Sandbox is null ? null : new
                    {
                        kind = invocation.Value.Sandbox.Kind.ToString(),
                        invocation.Value.Sandbox.IsAvailable,
                        supportedFeatures = invocation.Value.Sandbox.SupportedFeatures.ToString(),
                        invocation.Value.Sandbox.Evidence,
                    },
                    requestHash = invocation.Value.Invocation.RequestHash,
                    executed = true,
                    correlationId = approved.CorrelationId.Value,
                };
            }

            session.ToolInvocationPreviews.TryRemove(request.PreviewHash, out _);
            StoreIdempotentResult(session, scopedKey, requestHash, response);
            return Results.Ok(response);
        }
        finally
        {
            session.MutationGate.Release();
        }
    }

    private static DomainResult<IReadOnlyDictionary<string, ToolParameterValue>> ConvertParameters(
        ToolDescriptor descriptor,
        IReadOnlyDictionary<string, JsonElement> supplied)
    {
        var declared = descriptor.Definition.Parameters.ToDictionary(item => item.Name, StringComparer.Ordinal);
        if (supplied.Keys.Any(item => !declared.ContainsKey(item)))
        {
            return DomainResult.Fail<IReadOnlyDictionary<string, ToolParameterValue>>(new DomainFailure(
                FailureCode.ValidationFailure,
                "Tool values must match the exact descriptor parameter schema."));
        }

        var converted = new Dictionary<string, ToolParameterValue>(StringComparer.Ordinal);
        foreach (var pair in supplied)
        {
            var parameter = declared[pair.Key];
            ToolParameterValue? value = parameter.Type switch
            {
                ToolParameterType.Text when pair.Value.ValueKind is JsonValueKind.String =>
                    new ToolParameterValue(ToolParameterValueKind.Text, pair.Value.GetString(), null, null),
                ToolParameterType.WholeNumber when pair.Value.ValueKind is JsonValueKind.Number &&
                    pair.Value.TryGetInt64(out var number) =>
                    new ToolParameterValue(ToolParameterValueKind.WholeNumber, null, number, null),
                ToolParameterType.Switch when pair.Value.ValueKind is JsonValueKind.True or JsonValueKind.False =>
                    new ToolParameterValue(ToolParameterValueKind.Switch, null, null, pair.Value.GetBoolean()),
                _ => null,
            };
            if (value is null)
            {
                return DomainResult.Fail<IReadOnlyDictionary<string, ToolParameterValue>>(new DomainFailure(
                    FailureCode.ValidationFailure,
                    $"Parameter '{pair.Key}' does not match its descriptor type."));
            }

            converted.Add(pair.Key, value);
        }

        return DomainResult.Success<IReadOnlyDictionary<string, ToolParameterValue>>(converted);
    }
}
