using AgentForge.Abstractions.Agents;
using AgentForge.Abstractions.Installations;
using AgentForge.Abstractions.Models;
using AgentForge.Abstractions.Orchestration;
using AgentForge.Abstractions.Providers;
using AgentForge.Abstractions.Runtime;
using AgentForge.Abstractions.Time;
using AgentForge.Domain.Agents;
using AgentForge.Domain.Models;
using AgentForge.Domain.Orchestration;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Providers;
using AgentForge.Domain.Runtime;

namespace AgentForge.Host.Http;

internal sealed record ContinueRunConversationRequest(
    string Prompt,
    string? ResponseDepth,
    int? MaximumOutputTokens);

internal static partial class ReadyAdminEndpoints
{
    private const string ConversationStreamOwner = "ready-ui:durable-conversation";

    private static async Task<IResult> GetRunConversationAsync(
        Guid conversationId,
        HttpContext context,
        ReadyAdminSessionManager sessions,
        IInstallationStateReader stateReader,
        IRunConversationService conversations,
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

        return Results.Ok(ConversationDetailsResponse(details.Value));
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
            authority.Value.Agent.Budget.MaxInputTokens);
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
        var currentTask = await taskSnapshots.FindLatestAsync(turn.TaskId, cancellationToken);
        if (currentTask is null)
        {
            var created = await orchestrator.CreateAsync(
                ConversationTaskDefinition(
                    details.Value.Snapshot, turn, authority.Value.Agent.Budget.MaxInputTokens),
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

            var observer = new SseInteractionObserver(context);
            var interaction = await interactions.InvokeAsync(new LocalModelInteractionRequest(
                new ModelRequestId(turn.TaskId.Value),
                provider,
                detailsBeforeTurn.SystemInstruction,
                detailsBeforeTurn.Turns.Single(item => item.Turn.Id == turn.Id).Prompt,
                new ModelInvocationLimits(
                    turn.MaximumOutputTokens,
                    0,
                    Math.Max(4_096, Math.Min(MaximumInteractiveEvents, turn.MaximumOutputTokens + 512)),
                    turn.MaximumWallClockSeconds),
                started.Snapshot.CorrelationId,
                ConversationHistory(detailsBeforeTurn, turn.Id)), observer, linkedCancellation.Token);
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

    private static ModelMessage[] ConversationHistory(
        RunConversationDetails details,
        RunConversationTurnId currentTurnId)
    {
        const int maximumCharacters = 100_000;
        var selected = new List<RunConversationTurnContent>();
        var characters = 0;
        foreach (var item in details.Turns
            .Where(item => item.Turn.Id != currentTurnId && item.Turn.State is RunConversationTurnState.Completed)
            .Reverse())
        {
            var next = item.Prompt.Length + (item.Response?.Length ?? 0);
            if (selected.Count >= 20 || characters + next > maximumCharacters) break;
            selected.Add(item);
            characters += next;
        }
        selected.Reverse();
        return selected.SelectMany(item => new[]
        {
            new ModelMessage(ModelMessageRole.User, [new ModelTextContent(item.Prompt)]),
            new ModelMessage(ModelMessageRole.Assistant, [new ModelTextContent(item.Response!)]),
        }).ToArray();
    }

    private static OrchestrationTaskDefinition ConversationTaskDefinition(
        RunConversationSnapshot conversation,
        RunConversationTurn turn,
        long maximumInputTokens) => new(
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
                0,
                maximumInputTokens,
                turn.MaximumOutputTokens,
                turn.MaximumWallClockSeconds),
            new TaskRetryPolicy(2, 0))],
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

    private static object ConversationDetailsResponse(RunConversationDetails details) => new
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

    private static bool ValidContinuation(ContinueRunConversationRequest? request)
    {
        if (request is null || !PromptText(request.Prompt, 16_384)) return false;
        var depth = string.IsNullOrWhiteSpace(request.ResponseDepth)
            ? "balanced"
            : request.ResponseDepth.Trim().ToLowerInvariant();
        return depth is "concise" or "balanced" or "detailed" or "extended" or "maximum" &&
            request.MaximumOutputTokens is null or >= 1 and <= MaximumInteractiveOutputTokens;
    }
}
