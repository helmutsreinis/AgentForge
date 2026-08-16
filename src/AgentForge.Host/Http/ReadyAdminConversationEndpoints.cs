using System.Text;
using System.Text.Json;
using AgentForge.Abstractions.Agents;
using AgentForge.Abstractions.Installations;
using AgentForge.Abstractions.Models;
using AgentForge.Abstractions.Orchestration;
using AgentForge.Abstractions.Providers;
using AgentForge.Abstractions.Runtime;
using AgentForge.Abstractions.Security;
using AgentForge.Abstractions.Setup;
using AgentForge.Abstractions.Time;
using AgentForge.Abstractions.Tools;
using AgentForge.Domain.Agents;
using AgentForge.Domain.Models;
using AgentForge.Domain.Orchestration;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Providers;
using AgentForge.Domain.Runtime;
using AgentForge.Domain.Security;
using AgentForge.Domain.Tools;

namespace AgentForge.Host.Http;

internal sealed record ContinueRunConversationRequest(
    string Prompt,
    string? ResponseDepth,
    int? MaximumOutputTokens);

internal sealed record ConversationSearchPreviewRequest(
    string Disposition = "grant",
    int ApprovalSeconds = 300);

internal sealed record PreparedConversationContext(
    IReadOnlyList<ModelMessage> History,
    long CapacityTokens,
    long EstimatedInputTokens,
    int ReservedOutputTokens,
    long CompressionThresholdTokens,
    long CompressionTargetTokens,
    int OccupancyPercent,
    bool WasCompressed,
    int CompressedTurnCount,
    int ProtectedTurnCount);

internal static partial class ReadyAdminEndpoints
{
    private const string ConversationStreamOwner = "ready-ui:durable-conversation";
    private const string BraveSearchEndpoint = "https://api.search.brave.com/res/v1/web/search";

    private static async Task<IResult> PreviewConversationSearchAsync(
        Guid conversationId,
        Guid turnId,
        ConversationSearchPreviewRequest request,
        HttpContext context,
        ReadyAdminSessionManager sessions,
        IInstallationStateReader stateReader,
        IToolInvocationPlanner planner,
        ICapabilityApprovalService approvalService,
        ILocalAdministratorRepository administrators,
        ISecretStore secretStore,
        IRunConversationService conversations,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var acquired = await AcquireMutationAsync(context, sessions, stateReader, clock, cancellationToken);
        if (acquired.Failure is not null) return acquired.Failure;
        if (!string.Equals(request.Disposition, "grant", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(request.Disposition, "deny", StringComparison.OrdinalIgnoreCase))
        {
            return Problem(context, 400, "Invalid search decision",
                "Choose either grant or deny for this exact search request.", "validation-failure");
        }
        var disposition = string.Equals(request.Disposition, "deny", StringComparison.OrdinalIgnoreCase)
            ? CapabilityApprovalDisposition.Deny
            : CapabilityApprovalDisposition.Grant;
        if (request.ApprovalSeconds is < 30 or > 600)
        {
            return Problem(context, 400, "Invalid approval lifetime",
                "Choose an exact approval lifetime from 30 to 600 seconds.", "validation-failure");
        }

        var session = acquired.Session!;
        var details = await conversations.GetDetailsAsync(new RunConversationId(conversationId), cancellationToken);
        if (!details.IsSuccess || details.Value.Snapshot.InstallationId != session.InstallationId)
        {
            return Problem(context, 404, "Search request not found",
                "No matching durable agent search request exists.", "not-found");
        }
        var pending = details.Value.Snapshot.ToolCalls.SingleOrDefault(item =>
            item.TurnId.Value == turnId && item.State is RunConversationToolCallState.AwaitingApproval);
        if (pending is null || !string.Equals(pending.ToolName, "search_web", StringComparison.Ordinal))
        {
            return Problem(context, 409, "Search approval is not pending",
                "Reload the run; its current turn has no search request awaiting approval.", "concurrency-conflict");
        }
        var parameters = ConversationSearchParameters(pending.ArgumentsJson);
        if (!parameters.IsSuccess) return DomainProblem(context, parameters.Failure!, "Search request is invalid");

        var installation = await stateReader.ReadAsync(cancellationToken);
        var correlation = new CorrelationId($"run-search:{conversationId:D}:{turnId:D}");
        var workspace = Path.GetFullPath(AppContext.BaseDirectory);
        var planned = await planner.PlanAsync(new ToolInvocationPlanRequest(
            installation.Version,
            details.Value.Snapshot.AgentId,
            details.Value.Snapshot.AgentVersion,
            session.ActorId,
            "tool:search.brave",
            "1.0.0",
            parameters.Value,
            workspace,
            correlation,
            details.Value.Snapshot.CorrelationId), cancellationToken);
        if (!planned.IsSuccess) return DomainProblem(context, planned.Failure!, "Search request denied");

        var credential = await MaterializeAdministratorCredentialAsync(
            session.InstallationId, administrators, secretStore, cancellationToken);
        if (!credential.IsSuccess) return DomainProblem(context, credential.Failure!, "Search approval preview failed");
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
        if (!preview.IsSuccess) return DomainProblem(context, preview.Failure!, "Search approval preview failed");

        RetainBoundedPreviews(session.ConversationToolPreviews, 7);
        session.ConversationToolPreviews[preview.Value.PreviewHash] = new ReadyConversationToolPreview(
            details.Value.Snapshot.Id,
            pending.TurnId,
            pending.ToolCallId,
            new ReadyToolInvocationPreview(
                planned.Value, parameters.Value, disposition, expiresAt, correlation, preview.Value.PreviewHash));
        return Results.Ok(new
        {
            previewHash = preview.Value.PreviewHash,
            requestHash = preview.Value.RequestHash,
            disposition = disposition.ToString(),
            expiresAt,
            query = parameters.Value["query"].Text,
            maximumResults = parameters.Value["maximumResults"].WholeNumber,
            endpoint = BraveSearchEndpoint,
            risk = "Credential-isolated read from one fixed endpoint",
            warning = disposition is CapabilityApprovalDisposition.Grant
                ? "This exact query is approved once. The credential remains OS-backed and hidden from the model."
                : "The model receives a denial result and no network request is made.",
        });
    }

    private static async Task<IResult> ApplyConversationSearchAsync(
        Guid conversationId,
        Guid turnId,
        ReadyEditApplyWebRequest request,
        HttpContext context,
        ReadyAdminSessionManager sessions,
        IInstallationStateReader stateReader,
        ICapabilityApprovalService approvalService,
        IToolInvocationService invocationService,
        ILocalAdministratorRepository administrators,
        ISecretStore secretStore,
        IRunConversationService conversations,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var acquired = await AcquireMutationAsync(context, sessions, stateReader, clock, cancellationToken);
        if (acquired.Failure is not null) return acquired.Failure;
        var session = acquired.Session!;
        if (!Text(request.PreviewHash, 128) ||
            !session.ConversationToolPreviews.TryGetValue(request.PreviewHash, out var stored) ||
            stored.ConversationId.Value != conversationId || stored.TurnId.Value != turnId ||
            stored.Invocation.ExpiresAt <= clock.UtcNow)
        {
            return Problem(context, 409, "Search preview unavailable",
                "Preview this exact pending query again before applying the decision.", "approval-expired");
        }

        var details = await conversations.GetDetailsAsync(stored.ConversationId, cancellationToken);
        var pending = details.IsSuccess
            ? details.Value.Snapshot.ToolCalls.SingleOrDefault(item =>
                item.TurnId == stored.TurnId && item.ToolCallId == stored.ToolCallId &&
                item.State is RunConversationToolCallState.AwaitingApproval)
            : null;
        if (pending is null)
        {
            return Problem(context, 409, "Search request changed",
                "The pending run changed after preview; reload it before continuing.", "concurrency-conflict");
        }

        var credential = await MaterializeAdministratorCredentialAsync(
            session.InstallationId, administrators, secretStore, cancellationToken);
        if (!credential.IsSuccess) return DomainProblem(context, credential.Failure!, "Search approval failed");
        DomainResult<CapabilityApproval> approval;
        await using (var lease = credential.Value)
        {
            approval = await approvalService.ApplyAsync(new ApplyCapabilityApprovalRequest(
                stored.Invocation.Plan.Invocation,
                stored.Invocation.Disposition,
                stored.Invocation.ExpiresAt,
                stored.Invocation.PreviewHash,
                $"run-search-approval:{conversationId:D}:{turnId:D}:{pending.ToolCallId}",
                session.ActorId,
                stored.Invocation.CorrelationId,
                lease.Value), cancellationToken);
        }
        if (!approval.IsSuccess) return DomainProblem(context, approval.Failure!, "Search approval failed");

        string resultJson;
        var denied = stored.Invocation.Disposition is CapabilityApprovalDisposition.Deny;
        var isError = denied;
        Guid? invocationId = null;
        if (denied)
        {
            resultJson = JsonSerializer.Serialize(new
            {
                error = "The operator denied this exact search query.",
                citations = Array.Empty<object>(),
            });
        }
        else
        {
            var invocation = await invocationService.InvokeAsync(new ToolInvocationRequest(
                stored.Invocation.Plan.Invocation.InstallationVersion,
                stored.Invocation.Plan.Invocation.AgentId,
                stored.Invocation.Plan.Invocation.AgentVersion,
                session.ActorId,
                "tool:search.brave",
                "1.0.0",
                stored.Invocation.Parameters,
                stored.Invocation.Plan.Authorization.NormalizedWorkspace!,
                $"run-search:{conversationId:D}:{turnId:D}:{pending.ToolCallId}",
                stored.Invocation.CorrelationId,
                details.Value.Snapshot.CorrelationId), null, cancellationToken);
            if (!invocation.IsSuccess) return DomainProblem(context, invocation.Failure!, "Approved search failed");
            resultJson = Encoding.UTF8.GetString(invocation.Value.StandardOutput);
            invocationId = invocation.Value.Invocation.Id.Value;
        }

        var resolved = await conversations.ResolveToolCallAsync(
            stored.ConversationId,
            details.Value.Snapshot.Version,
            stored.TurnId,
            stored.ToolCallId,
            resultJson,
            isError,
            denied,
            cancellationToken);
        if (!resolved.IsSuccess) return DomainProblem(context, resolved.Failure!, "Search result could not be attached");
        session.ConversationToolPreviews.TryRemove(request.PreviewHash, out _);
        return Results.Ok(new
        {
            approvalId = approval.Value.Id.Value,
            invocationId,
            denied,
            executed = !denied,
            conversationVersion = resolved.Value.Snapshot.Version,
            resumePath = $"/api/v1/admin/runs/{conversationId:D}/turns/{turnId:D}/resume-stream",
        });
    }

    private static DomainResult<IReadOnlyDictionary<string, ToolParameterValue>> ConversationSearchParameters(
        string argumentsJson)
    {
        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            var root = document.RootElement;
            var properties = root.ValueKind is JsonValueKind.Object
                ? root.EnumerateObject().ToArray()
                : [];
            if (root.ValueKind is not JsonValueKind.Object || properties.Length is > 2 ||
                properties.Select(item => item.Name).Distinct(StringComparer.Ordinal).Count() != properties.Length ||
                properties.Any(item => item.Name is not ("query" or "maximumResults")) ||
                !root.TryGetProperty("query", out var queryValue) ||
                queryValue.ValueKind is not JsonValueKind.String ||
                string.IsNullOrWhiteSpace(queryValue.GetString()) || queryValue.GetString()!.Length > 512)
            {
                return InvalidSearchParameters();
            }
            var maximumResults = 5L;
            if (root.TryGetProperty("maximumResults", out var maximumValue) &&
                (maximumValue.ValueKind is not JsonValueKind.Number ||
                !maximumValue.TryGetInt64(out maximumResults) || maximumResults is < 1 or > 10))
            {
                return InvalidSearchParameters();
            }
            return DomainResult.Success<IReadOnlyDictionary<string, ToolParameterValue>>(
                new Dictionary<string, ToolParameterValue>(StringComparer.Ordinal)
                {
                    ["query"] = new(ToolParameterValueKind.Text, queryValue.GetString()!.Trim(), null, null),
                    ["maximumResults"] = new(ToolParameterValueKind.WholeNumber, null, maximumResults, null),
                    ["endpoint"] = new(ToolParameterValueKind.Text, BraveSearchEndpoint, null, null),
                });
        }
        catch (JsonException)
        {
            return InvalidSearchParameters();
        }
    }

    private static DomainResult<IReadOnlyDictionary<string, ToolParameterValue>> InvalidSearchParameters() =>
        DomainResult.Fail<IReadOnlyDictionary<string, ToolParameterValue>>(new DomainFailure(
            FailureCode.ValidationFailure,
            "The model search request must contain one bounded query and an optional result count from 1 to 10."));

    private static async Task<IResult> GetRunConversationAsync(
        Guid conversationId,
        HttpContext context,
        ReadyAdminSessionManager sessions,
        IInstallationStateReader stateReader,
        IRunConversationService conversations,
        IAgentIdentityRepository agents,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var acquired = await AcquireAsync(
            context, sessions, stateReader, clock, requireCsrf: false, cancellationToken);
        if (acquired.Failure is not null) return acquired.Failure;

        var details = await conversations.GetDetailsAsync(
            new RunConversationId(conversationId), cancellationToken);
        if (!details.IsSuccess || details.Value.Snapshot.InstallationId != acquired.Session!.InstallationId)
        {
            return Problem(context, 404, "Run conversation not found",
                "No durable conversation exists under this installation.", "not-found");
        }

        var agent = await agents.FindByIdAsync(details.Value.Snapshot.AgentId, cancellationToken);
        return Results.Ok(ConversationDetailsResponse(details.Value, agent?.Budget));
    }

    private static async Task ContinueRunConversationAsync(
        Guid conversationId,
        HttpContext context,
        ContinueRunConversationRequest request,
        ReadyAdminSessionManager sessions,
        IInstallationStateReader stateReader,
        IAgentIdentityRepository agents,
        IProviderProfileRepository providers,
        ITaskOrchestrator orchestrator,
        IRunConversationService conversations,
        ILocalModelInteractionService interactions,
        ReadyActiveInteractionRegistry activeInteractions,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var acquired = await AcquireMutationAsync(
            context, sessions, stateReader, clock, cancellationToken);
        if (acquired.Failure is not null)
        {
            await acquired.Failure.ExecuteAsync(context);
            return;
        }
        if (!ValidContinuation(request))
        {
            await Problem(context, 400, "Invalid conversation turn",
                "Enter a bounded prompt and choose a supported output preset and token limit.",
                "validation-failure").ExecuteAsync(context);
            return;
        }

        var session = acquired.Session!;
        var details = await conversations.GetDetailsAsync(
            new RunConversationId(conversationId), cancellationToken);
        if (!details.IsSuccess || details.Value.Snapshot.InstallationId != session.InstallationId)
        {
            await Problem(context, 404, "Run conversation not found",
                "No durable conversation exists under this installation.", "not-found").ExecuteAsync(context);
            return;
        }
        if (details.Value.Snapshot.State is not RunConversationState.Ready)
        {
            await Problem(context, 409, "Conversation is not ready",
                "Complete, resume, or cancel the current turn before adding another.",
                "concurrency-conflict").ExecuteAsync(context);
            return;
        }

        var authority = await ReadConversationAuthorityAsync(
            details.Value.Snapshot, session.InstallationId, agents, providers, cancellationToken);
        if (!authority.IsSuccess)
        {
            await DomainProblem(context, authority.Failure!, "Pinned conversation authority changed")
                .ExecuteAsync(context);
            return;
        }

        var idempotencyKey = context.Request.Headers["Idempotency-Key"].ToString();
        var identity = StableRequestIdentity(
            session.InstallationId, $"conversation-turn:{conversationId:D}", idempotencyKey);
        var taskId = new OrchestrationTaskId(new Guid(identity.AsSpan(0, 16)));
        var turnId = new RunConversationTurnId(new Guid(identity.AsSpan(16, 16)));
        var depth = string.IsNullOrWhiteSpace(request.ResponseDepth)
            ? "balanced"
            : request.ResponseDepth.Trim().ToLowerInvariant();
        var ceiling = (int)Math.Clamp(
            authority.Value.Agent.Budget.MaxOutputTokens, 1L, MaximumInteractiveOutputTokens);
        if (request.MaximumOutputTokens is { } requested && (requested < 1 || requested > ceiling))
        {
            await Problem(context, 422, "Output budget exceeded",
                $"Choose an output-token limit between 1 and the pinned agent ceiling of {ceiling:N0}.",
                "budget-exceeded").ExecuteAsync(context);
            return;
        }
        var maximumOutputTokens = request.MaximumOutputTokens ??
            ResponseTokenLimit(depth, authority.Value.Agent.Budget.MaxOutputTokens);
        var maximumWallClockSeconds = Math.Clamp(
            authority.Value.Agent.Budget.MaxWallClockSeconds, 1, MaximumInteractiveWallClockSeconds);
        var correlation = new CorrelationId($"conversation:{Convert.ToHexStringLower(identity)}");

        var added = await conversations.AddTurnAsync(new AddRunConversationTurnRequest(
            details.Value.Snapshot.Id,
            details.Value.Snapshot.Version,
            turnId,
            taskId,
            request.Prompt,
            depth,
            maximumOutputTokens,
            maximumWallClockSeconds,
            StoredIdempotencyKey("conversation-turn", idempotencyKey)), cancellationToken);
        if (!added.IsSuccess)
        {
            await DomainProblem(context, added.Failure!, "Conversation turn could not be stored")
                .ExecuteAsync(context);
            return;
        }
        if (added.Value.WasReplay)
        {
            await Problem(context, 409, "Stream cannot be replayed",
                "Open the durable run details or use Resume for an interrupted turn.",
                "stream-replay-denied").ExecuteAsync(context);
            return;
        }

        var definition = ConversationTaskDefinition(
            details.Value.Snapshot,
            added.Value.Turn,
            authority.Value.Agent.Budget.MaxInputTokens,
            authority.Value.Agent.Budget.MaxToolInvocations);
        var created = await orchestrator.CreateAsync(
            definition,
            session.ActorId,
            StoredIdempotencyKey("conversation-task", idempotencyKey),
            correlation,
            new CorrelationId($"run:{conversationId:D}"),
            cancellationToken);
        if (!created.IsSuccess)
        {
            await conversations.FailTurnAsync(
                details.Value.Snapshot.Id,
                added.Value.Snapshot.Version,
                turnId,
                created.Failure!.Code,
                retryable: false,
                SnapshotHash(new { TaskId = taskId.Value, created.Failure.Code }),
                CancellationToken.None);
            await DomainProblem(context, created.Failure, "Conversation task could not be created")
                .ExecuteAsync(context);
            return;
        }

        var claim = await orchestrator.ClaimAsync(
            taskId,
            created.Value.Snapshot.Version,
            new TaskNodeId("local-model"),
            ConversationStreamOwner,
            TimeSpan.FromSeconds(Math.Min(maximumWallClockSeconds + 30, 300)),
            cancellationToken);
        if (!claim.IsSuccess)
        {
            await DomainProblem(context, claim.Failure!, "Conversation task could not start")
                .ExecuteAsync(context);
            return;
        }
        var started = await conversations.StartTurnAsync(
            details.Value.Snapshot.Id, added.Value.Snapshot.Version, turnId, cancellationToken);
        if (!started.IsSuccess)
        {
            await DomainProblem(context, started.Failure!, "Conversation turn could not start")
                .ExecuteAsync(context);
            return;
        }
        var executionDetails = await conversations.GetDetailsAsync(
            details.Value.Snapshot.Id, cancellationToken);
        if (!executionDetails.IsSuccess)
        {
            await DomainProblem(context, executionDetails.Failure!, "Stored conversation turn could not be opened")
                .ExecuteAsync(context);
            return;
        }

        await ExecuteConversationTurnAsync(
            context,
            session,
            executionDetails.Value,
            started.Value,
            authority.Value.Agent,
            authority.Value.Provider,
            claim.Value,
            interactions,
            orchestrator,
            conversations,
            activeInteractions,
            cancellationToken);
    }

    private static async Task ResumeRunConversationAsync(
        Guid conversationId,
        Guid turnId,
        HttpContext context,
        ReadyAdminSessionManager sessions,
        IInstallationStateReader stateReader,
        IAgentIdentityRepository agents,
        IProviderProfileRepository providers,
        ITaskOrchestrator orchestrator,
        ITaskSnapshotStore taskSnapshots,
        IRunConversationService conversations,
        ILocalModelInteractionService interactions,
        ReadyActiveInteractionRegistry activeInteractions,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var acquired = await AcquireMutationAsync(
            context, sessions, stateReader, clock, cancellationToken);
        if (acquired.Failure is not null)
        {
            await acquired.Failure.ExecuteAsync(context);
            return;
        }

        var session = acquired.Session!;
        var details = await conversations.GetDetailsAsync(new RunConversationId(conversationId), cancellationToken);
        if (!details.IsSuccess || details.Value.Snapshot.InstallationId != session.InstallationId ||
            details.Value.Snapshot.Turns[^1].Id.Value != turnId)
        {
            await Problem(context, 404, "Resumable turn not found",
                "No matching current turn exists under this installation.", "not-found").ExecuteAsync(context);
            return;
        }
        if (details.Value.Snapshot.State is not (
            RunConversationState.Running or RunConversationState.NeedsResume))
        {
            await Problem(context, 409, "Turn is not resumable",
                "Only an interrupted or crash-orphaned current turn can be resumed.",
                "concurrency-conflict").ExecuteAsync(context);
            return;
        }

        var authority = await ReadConversationAuthorityAsync(
            details.Value.Snapshot, session.InstallationId, agents, providers, cancellationToken);
        if (!authority.IsSuccess)
        {
            await DomainProblem(context, authority.Failure!, "Pinned conversation authority changed")
                .ExecuteAsync(context);
            return;
        }

        var turn = details.Value.Snapshot.Turns[^1];
        if (details.Value.Snapshot.ToolCalls.Any(item => item.TurnId == turn.Id &&
            item.State is RunConversationToolCallState.AwaitingApproval))
        {
            await Problem(context, 409, "Search approval required",
                "Approve or deny the exact pending search request before resuming this turn.",
                "approval-required").ExecuteAsync(context);
            return;
        }
        var currentTask = await taskSnapshots.FindLatestAsync(turn.TaskId, cancellationToken);
        if (currentTask is null)
        {
            var created = await orchestrator.CreateAsync(
                ConversationTaskDefinition(
                    details.Value.Snapshot,
                    turn,
                    authority.Value.Agent.Budget.MaxInputTokens,
                    authority.Value.Agent.Budget.MaxToolInvocations),
                session.ActorId,
                $"resume:{turn.IdempotencyKey}",
                new CorrelationId($"resume:{turn.TaskId.Value:D}"),
                new CorrelationId($"run:{conversationId:D}"),
                cancellationToken);
            if (!created.IsSuccess)
            {
                await DomainProblem(context, created.Failure!, "Missing conversation task could not be recovered")
                    .ExecuteAsync(context);
                return;
            }
            currentTask = created.Value.Snapshot;
        }

        var node = currentTask.Nodes.Single(item => item.Definition.Id == new TaskNodeId("local-model"));
        if (node is { State: TaskNodeState.Leased, Lease: { } lease })
        {
            if (lease.ExpiresAt > clock.UtcNow)
            {
                await Problem(context, 409, "Turn lease is still active",
                    $"Retry Resume after {lease.ExpiresAt:O}; the previous worker lease has not expired.",
                    "lease-active").ExecuteAsync(context);
                return;
            }
            var recovered = await orchestrator.RecoverExpiredAsync(
                currentTask.Definition.Id, currentTask.Version, cancellationToken);
            if (!recovered.IsSuccess)
            {
                await DomainProblem(context, recovered.Failure!, "Expired conversation lease could not recover")
                    .ExecuteAsync(context);
                return;
            }
            currentTask = recovered.Value.Snapshot;
            node = currentTask.Nodes.Single(item => item.Definition.Id == new TaskNodeId("local-model"));
        }
        if (node.State is not TaskNodeState.Ready)
        {
            await Problem(context, 409, "Turn task is not claimable",
                "The durable task is terminal or has no ready retry remaining.",
                "concurrency-conflict").ExecuteAsync(context);
            return;
        }

        var claim = await orchestrator.ClaimAsync(
            currentTask.Definition.Id,
            currentTask.Version,
            node.Definition.Id,
            ConversationStreamOwner,
            TimeSpan.FromSeconds(Math.Min(turn.MaximumWallClockSeconds + 30, 300)),
            cancellationToken);
        if (!claim.IsSuccess)
        {
            await DomainProblem(context, claim.Failure!, "Resumed conversation task could not start")
                .ExecuteAsync(context);
            return;
        }
        var started = await conversations.StartTurnAsync(
            details.Value.Snapshot.Id,
            details.Value.Snapshot.Version,
            turn.Id,
            cancellationToken);
        if (!started.IsSuccess)
        {
            await DomainProblem(context, started.Failure!, "Conversation resume could not be recorded")
                .ExecuteAsync(context);
            return;
        }

        await ExecuteConversationTurnAsync(
            context,
            session,
            details.Value,
            started.Value,
            authority.Value.Agent,
            authority.Value.Provider,
            claim.Value,
            interactions,
            orchestrator,
            conversations,
            activeInteractions,
            cancellationToken);
    }

    private static async Task ExecuteConversationTurnAsync(
        HttpContext context,
        ReadyAdminSession session,
        RunConversationDetails detailsBeforeTurn,
        RunConversationMutationResult started,
        AgentIdentity agent,
        ProviderProfile provider,
        TaskLeaseGrant claimed,
        ILocalModelInteractionService interactions,
        ITaskOrchestrator orchestrator,
        IRunConversationService conversations,
        ReadyActiveInteractionRegistry activeInteractions,
        CancellationToken cancellationToken)
    {
        var conversationId = started.Snapshot.Id;
        var turn = started.Turn;
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var active = new ReadyActiveInteraction(
            turn.TaskId, session.InstallationId, session.Hash, linkedCancellation);
        if (!activeInteractions.TryAdd(active))
        {
            await Problem(context, 409, "Interaction already active",
                "Only one stream may own this durable turn.", "concurrency-conflict").ExecuteAsync(context);
            return;
        }

        try
        {
            var preparedContext = PrepareConversationContext(
                detailsBeforeTurn,
                turn.Id,
                agent.Budget,
                turn.MaximumOutputTokens);
            if (!preparedContext.IsSuccess)
            {
                var evidence = SnapshotHash(new
                {
                    TaskId = turn.TaskId.Value,
                    FailureCode.BudgetExceeded,
                    agent.Budget.EffectiveContextWindowTokens,
                    turn.MaximumOutputTokens,
                });
                await orchestrator.FailAsync(
                    turn.TaskId,
                    claimed.Snapshot.Version,
                    new TaskNodeId("local-model"),
                    ConversationStreamOwner,
                    claimed.LeaseToken,
                    evidence,
                    FailureCode.BudgetExceeded,
                    retryable: false,
                    CancellationToken.None);
                await conversations.FailTurnAsync(
                    conversationId,
                    started.Snapshot.Version,
                    turn.Id,
                    FailureCode.BudgetExceeded,
                    retryable: false,
                    evidence,
                    CancellationToken.None);
                await DomainProblem(context, preparedContext.Failure!, "Conversation context exceeds its configured capacity")
                    .ExecuteAsync(context);
                return;
            }

            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "text/event-stream; charset=utf-8";
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["X-Accel-Buffering"] = "no";
            await context.Response.StartAsync(context.RequestAborted);
            await WriteSseAsync(context, "run-started", new
            {
                taskId = turn.TaskId.Value,
                conversationId = conversationId.Value,
                turnId = turn.Id.Value,
                state = "Running",
                resumed = turn.State is RunConversationTurnState.Running &&
                    detailsBeforeTurn.Snapshot.State is RunConversationState.NeedsResume,
                configuration = new
                {
                    name = started.Snapshot.Name,
                    turn = turn.Sequence,
                    responseDepth = turn.ResponseDepth,
                    maximumOutputTokens = turn.MaximumOutputTokens,
                    skillIds = started.Snapshot.SkillIds,
                    contextCapacityTokens = preparedContext.Value.CapacityTokens,
                    contextWindowSource = agent.Budget.ContextWindowSource,
                },
                provider = new
                {
                    id = provider.Id.Value,
                    provider.Name,
                    provider.ProviderType,
                    endpoint = provider.Endpoint.ToString(),
                    provider.Model,
                },
                correlationId = started.Snapshot.CorrelationId.Value,
            }, context.RequestAborted);
            await WriteSseAsync(context, "context-status", new
            {
                capacityTokens = preparedContext.Value.CapacityTokens,
                estimatedInputTokens = preparedContext.Value.EstimatedInputTokens,
                reservedOutputTokens = preparedContext.Value.ReservedOutputTokens,
                occupancyPercent = preparedContext.Value.OccupancyPercent,
                thresholdPercent = agent.Budget.ContextCompressionThresholdPercent,
                targetPercent = agent.Budget.ContextCompressionTargetPercent,
                compressionEnabled = agent.Budget.ContextCompressionEnabled,
                compressed = preparedContext.Value.WasCompressed,
                compressedTurnCount = preparedContext.Value.CompressedTurnCount,
                protectedTurnCount = preparedContext.Value.ProtectedTurnCount,
                source = agent.Budget.ContextWindowSource,
            }, context.RequestAborted);

            var observer = new SseInteractionObserver(context);
            var toolContinuation = ConversationToolContinuation(detailsBeforeTurn.Snapshot, turn.Id);
            var remainingToolCalls = Math.Max(0, agent.Budget.MaxToolInvocations -
                detailsBeforeTurn.Snapshot.ToolCalls.Count(item => item.TurnId == turn.Id));
            var searchTools = SearchToolEnabled(agent, provider) && remainingToolCalls > 0
                ? new[] { BraveSearchModelTool() }
                : [];
            var interactionProvider = SearchToolCallProfile(provider, searchTools.Length > 0);
            var interaction = await interactions.InvokeAsync(new LocalModelInteractionRequest(
                new ModelRequestId(turn.TaskId.Value),
                interactionProvider,
                detailsBeforeTurn.SystemInstruction,
                detailsBeforeTurn.Turns.Single(item => item.Turn.Id == turn.Id).Prompt,
                new ModelInvocationLimits(
                    turn.MaximumOutputTokens,
                    searchTools.Length > 0 ? 1 : 0,
                    Math.Max(4_096, Math.Min(MaximumInteractiveEvents, turn.MaximumOutputTokens + 512)),
                    turn.MaximumWallClockSeconds),
                started.Snapshot.CorrelationId,
                preparedContext.Value.History,
                searchTools,
                toolContinuation), observer, linkedCancellation.Token);
            if (!interaction.IsSuccess)
            {
                var evidence = SnapshotHash(new
                {
                    TaskId = turn.TaskId.Value,
                    interaction.Failure!.Code,
                    interaction.Failure.IsRetryable,
                });
                var taskFailed = await orchestrator.FailAsync(
                    turn.TaskId,
                    claimed.Snapshot.Version,
                    new TaskNodeId("local-model"),
                    ConversationStreamOwner,
                    claimed.LeaseToken,
                    evidence,
                    interaction.Failure.Code,
                    interaction.Failure.IsRetryable,
                    CancellationToken.None);
                var resumable = interaction.Failure.IsRetryable && taskFailed.IsSuccess &&
                    !OrchestrationTaskStateMachine.IsTerminal(taskFailed.Value.Snapshot.State);
                var conversationFailed = await conversations.FailTurnAsync(
                    conversationId,
                    started.Snapshot.Version,
                    turn.Id,
                    interaction.Failure.Code,
                    resumable,
                    evidence,
                    CancellationToken.None);
                await WriteSseAsync(context, "failed", new
                {
                    code = interaction.Failure.Code.ToString(),
                    interaction.Failure.Message,
                    resumable,
                    run = conversationFailed.IsSuccess
                        ? ConversationResponse(conversationFailed.Value.Snapshot)
                        : null,
                }, context.RequestAborted);
                return;
            }

            if (interaction.Value.ToolCalls.Count > 0)
            {
                var toolCall = interaction.Value.ToolCalls.Single();
                if (!string.Equals(toolCall.ToolName, "search_web", StringComparison.Ordinal) ||
                    searchTools.Length == 0)
                {
                    var policyEvidence = SnapshotHash(new
                    {
                        TaskId = turn.TaskId.Value,
                        toolCall.ToolName,
                        Code = FailureCode.PolicyDenied,
                    });
                    await orchestrator.FailAsync(
                        turn.TaskId,
                        claimed.Snapshot.Version,
                        new TaskNodeId("local-model"),
                        ConversationStreamOwner,
                        claimed.LeaseToken,
                        policyEvidence,
                        FailureCode.PolicyDenied,
                        retryable: false,
                        CancellationToken.None);
                    var conversationDenied = await conversations.FailTurnAsync(
                        conversationId,
                        started.Snapshot.Version,
                        turn.Id,
                        FailureCode.PolicyDenied,
                        retryable: false,
                        policyEvidence,
                        CancellationToken.None);
                    await WriteSseAsync(context, "failed", new
                    {
                        code = FailureCode.PolicyDenied.ToString(),
                        message = "The model requested a tool outside its exact agent policy.",
                        resumable = false,
                        run = conversationDenied.IsSuccess
                            ? ConversationResponse(conversationDenied.Value.Snapshot)
                            : null,
                    }, context.RequestAborted);
                    return;
                }
                var taskPaused = await orchestrator.FailAsync(
                    turn.TaskId,
                    claimed.Snapshot.Version,
                    new TaskNodeId("local-model"),
                    ConversationStreamOwner,
                    claimed.LeaseToken,
                    interaction.Value.EvidenceHash,
                    FailureCode.ApprovalRequired,
                    retryable: true,
                    CancellationToken.None);
                var resumable = taskPaused.IsSuccess &&
                    !OrchestrationTaskStateMachine.IsTerminal(taskPaused.Value.Snapshot.State);
                var conversationPaused = resumable
                    ? await conversations.AwaitToolApprovalAsync(
                        conversationId,
                        started.Snapshot.Version,
                        turn.Id,
                        toolCall,
                        interaction.Value.EvidenceHash,
                        CancellationToken.None)
                    : await conversations.FailTurnAsync(
                        conversationId,
                        started.Snapshot.Version,
                        turn.Id,
                        FailureCode.BudgetExceeded,
                        false,
                        interaction.Value.EvidenceHash,
                        CancellationToken.None);
                await WriteSseAsync(context, resumable ? "approval-required" : "failed", new
                {
                    code = resumable ? FailureCode.ApprovalRequired.ToString() : FailureCode.BudgetExceeded.ToString(),
                    message = resumable
                        ? "The agent requested an exact Brave Search query. Review it before network access."
                        : "The run exhausted its bounded tool-call attempts.",
                    resumable,
                    toolCall = resumable ? new
                    {
                        toolCall.ToolCallId,
                        toolCall.ToolName,
                        arguments = JsonSerializer.Deserialize<JsonElement>(toolCall.ArgumentsJson),
                    } : null,
                    run = conversationPaused.IsSuccess ? ConversationResponse(conversationPaused.Value.Snapshot) : null,
                }, context.RequestAborted);
                return;
            }

            var taskCompleted = await orchestrator.CompleteAsync(
                turn.TaskId,
                claimed.Snapshot.Version,
                new TaskNodeId("local-model"),
                ConversationStreamOwner,
                claimed.LeaseToken,
                interaction.Value.EvidenceHash,
                CancellationToken.None);
            if (!taskCompleted.IsSuccess)
            {
                await WriteSseAsync(context, "failed", new
                {
                    code = taskCompleted.Failure!.Code.ToString(),
                    taskCompleted.Failure.Message,
                }, context.RequestAborted);
                return;
            }
            var conversationCompleted = await conversations.CompleteTurnAsync(
                conversationId,
                started.Snapshot.Version,
                turn.Id,
                interaction.Value,
                CancellationToken.None);
            if (!conversationCompleted.IsSuccess)
            {
                await WriteSseAsync(context, "failed", new
                {
                    code = conversationCompleted.Failure!.Code.ToString(),
                    conversationCompleted.Failure.Message,
                }, context.RequestAborted);
                return;
            }
            await WriteSseAsync(context, "completed", new
            {
                requestId = interaction.Value.RequestId.Value,
                usage = interaction.Value.Usage,
                finishReason = interaction.Value.FinishReason.ToString(),
                interaction.Value.ContextRedactionCount,
                interaction.Value.EventCount,
                interaction.Value.EvidenceHash,
                run = ConversationResponse(conversationCompleted.Value.Snapshot),
            }, context.RequestAborted);
        }
        catch (OperationCanceledException) when (activeInteractions.WasCanceled(turn.TaskId))
        {
            await conversations.CancelTurnAsync(
                conversationId, started.Snapshot.Version, turn.Id, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            var evidence = SnapshotHash(new
            {
                TaskId = turn.TaskId.Value,
                FailureCode.RecoverableExternalFailure,
                Interrupted = true,
            });
            var taskFailed = await orchestrator.FailAsync(
                turn.TaskId,
                claimed.Snapshot.Version,
                new TaskNodeId("local-model"),
                ConversationStreamOwner,
                claimed.LeaseToken,
                evidence,
                FailureCode.RecoverableExternalFailure,
                retryable: true,
                CancellationToken.None);
            await conversations.FailTurnAsync(
                conversationId,
                started.Snapshot.Version,
                turn.Id,
                FailureCode.RecoverableExternalFailure,
                taskFailed.IsSuccess && !OrchestrationTaskStateMachine.IsTerminal(taskFailed.Value.Snapshot.State),
                evidence,
                CancellationToken.None);
        }
        finally
        {
            activeInteractions.Remove(turn.TaskId);
        }
    }

    private static DomainResult<PreparedConversationContext> PrepareConversationContext(
        RunConversationDetails details,
        RunConversationTurnId currentTurnId,
        AgentBudget budget,
        int reservedOutputTokens)
    {
        var completed = details.Turns
            .Where(item => item.Turn.Id != currentTurnId && item.Turn.State is RunConversationTurnState.Completed)
            .ToArray();
        var currentPrompt = details.Turns.Single(item => item.Turn.Id == currentTurnId).Prompt;
        var capacity = budget.EffectiveContextWindowTokens;
        var threshold = capacity * budget.ContextCompressionThresholdPercent / 100;
        var target = capacity * budget.ContextCompressionTargetPercent / 100;
        var fullHistory = TurnMessages(completed);
        var fullEstimate = EstimateInputTokens(details.SystemInstruction, currentPrompt, fullHistory);
        if (fullEstimate + reservedOutputTokens < threshold ||
            completed.Length <= budget.ContextProtectedRecentTurns)
        {
            if (fullEstimate + reservedOutputTokens > capacity)
            {
                return DomainResult.Fail<PreparedConversationContext>(new DomainFailure(
                    FailureCode.BudgetExceeded,
                    $"The protected context needs approximately {fullEstimate + reservedOutputTokens:N0} tokens, above the effective {capacity:N0}-token window."));
            }
            return DomainResult.Success(new PreparedConversationContext(
                fullHistory, capacity, fullEstimate, reservedOutputTokens, threshold, target,
                Occupancy(fullEstimate + reservedOutputTokens, capacity), false, 0, completed.Length));
        }

        if (!budget.ContextCompressionEnabled)
        {
            return fullEstimate + reservedOutputTokens <= capacity
                ? DomainResult.Success(new PreparedConversationContext(
                    fullHistory, capacity, fullEstimate, reservedOutputTokens, threshold, target,
                    Occupancy(fullEstimate + reservedOutputTokens, capacity), false, 0, completed.Length))
                : DomainResult.Fail<PreparedConversationContext>(new DomainFailure(
                    FailureCode.BudgetExceeded,
                    "Conversation context reached the configured window while compression is disabled."));
        }

        var protectedCount = Math.Min(budget.ContextProtectedRecentTurns, completed.Length);
        var protectedTurns = completed[^protectedCount..];
        var compactedTurns = completed[..^protectedCount];
        var protectedHistory = TurnMessages(protectedTurns);
        var protectedEstimate = EstimateInputTokens(details.SystemInstruction, currentPrompt, protectedHistory);
        if (protectedEstimate + reservedOutputTokens > capacity)
        {
            return DomainResult.Fail<PreparedConversationContext>(new DomainFailure(
                FailureCode.BudgetExceeded,
                "The system prompt, current objective, output reserve, and protected recent turns exceed the configured context window."));
        }

        var summaryBudgetTokens = Math.Max(0, target - protectedEstimate - reservedOutputTokens);
        var summary = ExtractiveConversationSummary(compactedTurns, summaryBudgetTokens);
        var history = new List<ModelMessage>(protectedHistory.Count + 1);
        if (summary.Length > 0)
        {
            history.Add(new ModelMessage(ModelMessageRole.Assistant,
                [new ModelTextContent(summary)], "agentforge-context-summary"));
        }
        history.AddRange(protectedHistory);
        var estimate = EstimateInputTokens(details.SystemInstruction, currentPrompt, history);
        if (estimate + reservedOutputTokens > capacity)
        {
            return DomainResult.Fail<PreparedConversationContext>(new DomainFailure(
                FailureCode.BudgetExceeded,
                "Compressed conversation context still exceeds the configured window; lower the output reserve or context override."));
        }
        return DomainResult.Success(new PreparedConversationContext(
            history, capacity, estimate, reservedOutputTokens, threshold, target,
            Occupancy(estimate + reservedOutputTokens, capacity), true, compactedTurns.Length, protectedCount));
    }

    private static List<ModelMessage> TurnMessages(IEnumerable<RunConversationTurnContent> turns) =>
        turns.SelectMany(item => new[]
        {
            new ModelMessage(ModelMessageRole.User, [new ModelTextContent(item.Prompt)]),
            new ModelMessage(ModelMessageRole.Assistant, [new ModelTextContent(item.Response!)]),
        }).ToList();

    private static long EstimateInputTokens(
        string systemInstruction,
        string currentPrompt,
        List<ModelMessage> history)
    {
        long characters = systemInstruction.Length + currentPrompt.Length;
        foreach (var message in history)
        {
            characters += message.Content.OfType<ModelTextContent>().Sum(item => item.Text.Length);
        }
        return Math.Max(1, (characters + 3) / 4 + (history.Count + 2) * 8L);
    }

    private static string ExtractiveConversationSummary(
        RunConversationTurnContent[] turns,
        long tokenBudget)
    {
        var characterBudget = (int)Math.Min(400_000, Math.Max(0, tokenBudget * 4));
        const string header = "Earlier conversation summary (reference only; follow the latest user objective):\n";
        if (turns.Length == 0 || characterBudget <= header.Length + 64) return string.Empty;
        var builder = new System.Text.StringBuilder(Math.Min(characterBudget, 16_384));
        builder.Append(header);
        var perTurn = Math.Max(96, (characterBudget - header.Length) / turns.Length);
        foreach (var item in turns)
        {
            if (builder.Length >= characterBudget) break;
            builder.Append("Turn ").Append(item.Turn.Sequence).Append(" user: ");
            AppendBounded(builder, item.Prompt, perTurn / 2, characterBudget);
            builder.Append("\nAssistant: ");
            AppendBounded(builder, item.Response ?? string.Empty, perTurn / 2, characterBudget);
            builder.Append('\n');
        }
        return builder.Length <= characterBudget
            ? builder.ToString()
            : builder.ToString(0, characterBudget);
    }

    private static void AppendBounded(
        System.Text.StringBuilder builder,
        string value,
        int maximum,
        int totalMaximum)
    {
        var available = Math.Max(0, Math.Min(maximum, totalMaximum - builder.Length));
        if (available == 0) return;
        if (value.Length <= available) builder.Append(value);
        else if (available > 1) builder.Append(value.AsSpan(0, available - 1)).Append('…');
    }

    private static int Occupancy(long inputTokens, long capacity) =>
        capacity <= 0 ? 100 : (int)Math.Clamp(inputTokens * 100 / capacity, 0, 100);

    private static OrchestrationTaskDefinition ConversationTaskDefinition(
        RunConversationSnapshot conversation,
        RunConversationTurn turn,
        long maximumInputTokens,
        int maximumToolCalls) => new(
        turn.TaskId,
        conversation.InstallationId,
        conversation.AgentId,
        conversation.AgentVersion,
        OrchestrationPattern.Sequential,
        [new TaskNodeDefinition(
            new TaskNodeId("local-model"),
            $"{conversation.Name} · turn {turn.Sequence}",
            [],
            [],
            [turn.PromptArtifact.ContentHash],
            new TaskExecutionBudget(
                maximumToolCalls,
                maximumInputTokens,
                turn.MaximumOutputTokens,
                turn.MaximumWallClockSeconds),
            new TaskRetryPolicy(Math.Clamp(maximumToolCalls + 2, 2, 32), 0))],
        1,
        0,
        0,
        conversation.PolicySnapshotHash,
        conversation.BudgetSnapshotHash,
        conversation.SkillSnapshotHash);

    private static async Task<DomainResult<(AgentIdentity Agent, ProviderProfile Provider)>>
        ReadConversationAuthorityAsync(
            RunConversationSnapshot conversation,
            InstallationId installationId,
            IAgentIdentityRepository agents,
            IProviderProfileRepository providers,
            CancellationToken cancellationToken)
    {
        var agent = await agents.FindByIdAsync(conversation.AgentId, cancellationToken);
        if (agent is null || agent.InstallationId != installationId || agent.Version != conversation.AgentVersion ||
            agent.ModelPolicy.PrimaryProviderProfileId != conversation.ProviderId ||
            agent.ModelPolicy.DataLocality is not ModelDataLocality.LocalOnly ||
            agent.ModelPolicy.AllowFallback)
        {
            return DomainResult.Fail<(AgentIdentity, ProviderProfile)>(new DomainFailure(
                FailureCode.PolicyDenied,
                "The pinned agent version or local-only model authority changed; start a new conversation."));
        }
        var provider = await providers.FindByIdAsync(conversation.ProviderId, cancellationToken);
        if (provider is null || provider.InstallationId != installationId ||
            provider.Version != conversation.ProviderVersion ||
            !string.Equals(provider.Model, conversation.ProviderModel, StringComparison.Ordinal))
        {
            return DomainResult.Fail<(AgentIdentity, ProviderProfile)>(new DomainFailure(
                FailureCode.ConcurrencyConflict,
                "The pinned provider version changed; start a new conversation to use the new model profile."));
        }
        return DomainResult.Success((agent, provider));
    }

    private static object ConversationDetailsResponse(RunConversationDetails details, AgentBudget? budget) => new
    {
        run = ConversationResponse(details.Snapshot),
        provider = new
        {
            id = details.Snapshot.ProviderId.Value,
            version = details.Snapshot.ProviderVersion,
            model = details.Snapshot.ProviderModel,
        },
        skillIds = details.Snapshot.SkillIds,
        systemInstructionHash = details.Snapshot.SystemInstructionArtifact.ContentHash,
        policySnapshotHash = details.Snapshot.PolicySnapshotHash,
        budgetSnapshotHash = details.Snapshot.BudgetSnapshotHash,
        skillSnapshotHash = details.Snapshot.SkillSnapshotHash,
        context = budget is null ? null : ConversationContextStatus(details, budget),
        toolCalls = details.Snapshot.ToolCalls.Select(item => new
        {
            turnId = item.TurnId.Value,
            item.ToolCallId,
            item.ToolName,
            arguments = JsonSerializer.Deserialize<JsonElement>(item.ArgumentsJson),
            state = item.State.ToString(),
            item.RequestHash,
            item.ResultHash,
            item.IsError,
            item.RequestedAt,
            item.UpdatedAt,
        }),
        turns = details.Turns.Select(item => new
        {
            id = item.Turn.Id.Value,
            item.Turn.Sequence,
            taskId = item.Turn.TaskId.Value,
            state = item.Turn.State.ToString(),
            item.Prompt,
            item.Response,
            item.Turn.ResponseDepth,
            item.Turn.MaximumOutputTokens,
            item.Turn.Usage,
            finishReason = item.Turn.FinishReason?.ToString(),
            failureCode = item.Turn.FailureCode?.ToString(),
            item.Turn.Retryable,
            item.Turn.RequestHash,
            item.Turn.ResponseHash,
            item.Turn.EvidenceHash,
            item.Turn.CreatedAt,
            item.Turn.UpdatedAt,
        }),
    };

    private static object ConversationContextStatus(RunConversationDetails details, AgentBudget budget)
    {
        var messages = TurnMessages(details.Turns.Where(
            item => item.Turn.State is RunConversationTurnState.Completed));
        var estimate = EstimateInputTokens(details.SystemInstruction, string.Empty, messages);
        var capacity = budget.EffectiveContextWindowTokens;
        return new
        {
            capacityTokens = capacity,
            estimatedInputTokens = estimate,
            occupancyPercent = Occupancy(estimate, capacity),
            discoveredTokens = budget.DiscoveredContextWindowTokens,
            overrideTokens = budget.ContextWindowOverrideTokens,
            source = budget.ContextWindowSource,
            compressionEnabled = budget.ContextCompressionEnabled,
            thresholdPercent = budget.ContextCompressionThresholdPercent,
            targetPercent = budget.ContextCompressionTargetPercent,
            protectedRecentTurns = budget.ContextProtectedRecentTurns,
            compressionDue = estimate >= capacity * budget.ContextCompressionThresholdPercent / 100,
        };
    }

    private static bool ValidContinuation(ContinueRunConversationRequest? request)
    {
        if (request is null || !PromptText(request.Prompt, 16_384)) return false;
        var depth = string.IsNullOrWhiteSpace(request.ResponseDepth)
            ? "balanced"
            : request.ResponseDepth.Trim().ToLowerInvariant();
        return depth is "concise" or "balanced" or "detailed" or "extended" or "maximum" &&
            request.MaximumOutputTokens is null or >= 1 and <= MaximumInteractiveOutputTokens;
    }

    private static bool SearchToolEnabled(AgentIdentity agent, ProviderProfile provider) =>
        agent.Budget.MaxToolInvocations > 0 && SupportsSearchToolTransport(provider) &&
        agent.CapabilityPolicy.NetworkPosture is NetworkPosture.ApprovedEndpointsOnly &&
        agent.CapabilityPolicy.ToolGrants.Contains("tool:search.web", StringComparer.Ordinal);

    private static bool SupportsSearchToolTransport(ProviderProfile provider) =>
        provider.Capabilities.ToolCalls || provider.ProviderType is "vllm" or "openai-compatible";

    private static ProviderProfile SearchToolCallProfile(ProviderProfile provider, bool enabled) =>
        enabled && !provider.Capabilities.ToolCalls
            ? provider with
            {
                Capabilities = provider.Capabilities with
                {
                    ToolCalls = true,
                    EvidenceSource = "operator-policy-override-compatible-tool-transport-v1",
                },
            }
            : provider;

    private static ModelToolDefinition BraveSearchModelTool() => new(
        "search_web",
        "Search the public web through AgentForge's configured Brave provider. Every exact query pauses for operator approval. Use returned citation URLs when answering.",
        """
        {"type":"object","additionalProperties":false,"properties":{"query":{"type":"string","minLength":1,"maxLength":512},"maximumResults":{"type":"integer","minimum":1,"maximum":10}},"required":["query"]}
        """);

    private static List<ModelMessage> ConversationToolContinuation(
        RunConversationSnapshot snapshot,
        RunConversationTurnId turnId)
    {
        var messages = new List<ModelMessage>();
        foreach (var call in snapshot.ToolCalls.Where(item => item.TurnId == turnId &&
            item.State is RunConversationToolCallState.Executed or RunConversationToolCallState.Denied))
        {
            messages.Add(new ModelMessage(ModelMessageRole.Assistant,
                [new ModelToolCallContent(call.ToolCallId, call.ToolName, call.ArgumentsJson)]));
            messages.Add(new ModelMessage(ModelMessageRole.Tool,
                [new ModelToolResultContent(call.ToolCallId, call.ToolName, call.ResultJson!, call.IsError)]));
        }
        return messages;
    }
}
