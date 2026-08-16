using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentForge.Abstractions.Agents;
using AgentForge.Abstractions.Installations;
using AgentForge.Abstractions.Learning;
using AgentForge.Abstractions.Models;
using AgentForge.Abstractions.Orchestration;
using AgentForge.Abstractions.Providers;
using AgentForge.Abstractions.Security;
using AgentForge.Abstractions.Skills;
using AgentForge.Abstractions.Time;
using AgentForge.Domain.Agents;
using AgentForge.Domain.Learning;
using AgentForge.Domain.Models;
using AgentForge.Domain.Orchestration;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Providers;
using AgentForge.Domain.Skills;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;

namespace AgentForge.Host.Http;

internal sealed record CreateMvpRunRequest(Guid AgentId, string Name);
internal sealed record TestAgentChatRequest(string Prompt);
internal sealed record StreamAgentChatRequest(
    string Prompt,
    string? Name,
    string? RunInstructions,
    string? ResponseDepth,
    IReadOnlyList<string>? SkillIds);
internal sealed record RunSkillBody(string Id, string Version, string PackageHash, string Body);
internal sealed record CaptureLearningSignalWebRequest(
    Guid SourceTaskId,
    string Kind,
    string Summary,
    int OccurrenceCount);

internal static partial class ReadyAdminEndpoints
{
    private static readonly JsonSerializerOptions HashJson = new(JsonSerializerDefaults.Web);

    public static void MapReadyAdminApi(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var group = app.MapGroup("/api/v1/admin").RequireRateLimiting("production-api");
        group.MapPost("/session", CreateSessionAsync);
        group.MapGet("/session", GetSessionAsync);
        group.MapDelete("/session", DeleteSessionAsync);
        group.MapGet("/agents", ListAgentsAsync);
        group.MapGet("/agents/{agentId:guid}/edit", GetAgentEditAsync);
        group.MapPost("/agents/{agentId:guid}/models/discover", DiscoverAgentModelsAsync);
        group.MapPost("/agents/{agentId:guid}/model/preview", PreviewAgentModelAsync);
        group.MapPost("/agents/{agentId:guid}/model/apply", ApplyAgentModelAsync);
        group.MapPost("/agents/{agentId:guid}/profile/preview", PreviewAgentProfileAsync);
        group.MapPost("/agents/{agentId:guid}/profile/apply", ApplyAgentProfileAsync);
        group.MapGet("/agents/{agentId:guid}/run-options", GetRunOptionsAsync);
        group.MapGet("/runs", ListRunsAsync);
        group.MapPost("/runs", CreateRunAsync);
        group.MapPost("/runs/{taskId:guid}/cancel", CancelRunAsync);
        group.MapPost("/agents/{agentId:guid}/test-chat", TestAgentChatAsync);
        group.MapPost("/agents/{agentId:guid}/test-chat-stream", StreamAgentChatAsync);
        group.MapGet("/skills", ListSkillsAsync);
        group.MapPost("/skills/seed/csharp-review/install", InstallSeedSkillAsync);
        group.MapGet("/learning/signals", ListLearningSignalsAsync);
        group.MapPost("/learning/signals", CaptureLearningSignalAsync);
    }

    private static async Task<IResult> CreateSessionAsync(
        HttpContext context,
        ReadyAdminSessionManager sessions,
        IInstallationStateReader stateReader,
        ILocalAdministratorRepository administrators,
        ISecretStore secretStore,
        ILocalAdministratorAuthenticator authenticator,
        IClock clock,
        IOptions<HostSecurityOptions> hostOptions,
        CancellationToken cancellationToken)
    {
        if (!IsTrustedLoopback(context))
        {
            if (!IsRemoteConnection(context) || !context.Request.IsHttps ||
                !ValidRemoteAccessCode(context, hostOptions.Value.RemoteAccessCode))
            {
                return Problem(context, 403, "Remote authentication required",
                    "Enter the temporary access code shown by the AgentForge host operator.",
                    "remote-authentication-required");
            }
        }

        var state = await stateReader.ReadAsync(cancellationToken);
        if (!state.IsReady)
        {
            return Problem(context, 503, "Setup required",
                "Complete first-run setup before opening the operator workspace.", "setup-required");
        }

        var administrator = await administrators.FindAsync(state.Id, cancellationToken);
        if (administrator is null)
        {
            return Problem(context, 503, "Administrator unavailable",
                "The Ready installation has no local administrator record.", "administrator-unavailable");
        }

        var materialized = await secretStore.MaterializeAsync(
            administrator.ClientCredentialReference, cancellationToken);
        if (!materialized.IsSuccess)
        {
            return Problem(context, 503, "Protected credential unavailable",
                "The OS-backed local administrator credential could not be materialized.", "credential-unavailable");
        }

        await using var credential = materialized.Value;
        var authentication = await authenticator.AuthenticateAsync(
            state.Id, credential.Value, cancellationToken);
        if (!authentication.IsSuccess || authentication.Value != administrator.ActorId)
        {
            return Problem(context, 401, "Authentication failed",
                "The protected local administrator credential did not validate.", "authentication-failed");
        }

        sessions.Revoke(context.Request.Cookies[ReadyAdminSessionManager.CookieName]);
        var created = sessions.Create(state.Id, authentication.Value, clock.UtcNow);
        context.Response.Cookies.Append(ReadyAdminSessionManager.CookieName, created.Token, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            Secure = context.Request.IsHttps,
            MaxAge = TimeSpan.FromMinutes(30),
            Path = "/api/v1/admin",
            IsEssential = true,
        });
        return Results.Ok(SessionResponse(created.Session));
    }

    private static async Task<IResult> GetSessionAsync(
        HttpContext context,
        ReadyAdminSessionManager sessions,
        IInstallationStateReader stateReader,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var acquired = await AcquireAsync(
            context, sessions, stateReader, clock, requireCsrf: false, cancellationToken);
        return acquired.Failure ?? Results.Ok(SessionResponse(acquired.Session!));
    }

    private static async Task<IResult> DeleteSessionAsync(
        HttpContext context,
        ReadyAdminSessionManager sessions,
        IInstallationStateReader stateReader,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var acquired = await AcquireAsync(
            context, sessions, stateReader, clock, requireCsrf: true, cancellationToken);
        if (acquired.Failure is not null)
        {
            return acquired.Failure;
        }

        sessions.Revoke(context.Request.Cookies[ReadyAdminSessionManager.CookieName]);
        context.Response.Cookies.Delete(ReadyAdminSessionManager.CookieName, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            Secure = context.Request.IsHttps,
            Path = "/api/v1/admin",
        });
        return Results.NoContent();
    }

    private static async Task<IResult> ListAgentsAsync(
        HttpContext context,
        ReadyAdminSessionManager sessions,
        IInstallationStateReader stateReader,
        IAgentIdentityRepository agents,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var acquired = await AcquireAsync(
            context, sessions, stateReader, clock, requireCsrf: false, cancellationToken);
        if (acquired.Failure is not null)
        {
            return acquired.Failure;
        }

        var items = await agents.ListAsync(acquired.Session!.InstallationId, cancellationToken);
        return Results.Ok(new
        {
            agents = items.Select(agent => new
            {
                id = agent.Id.Value,
                agent.Name,
                agent.Expertise,
                agent.Mission,
                agent.PreferredLanguage,
                agent.TimeZone,
                agent.ResponseStyle,
                agent.DefaultWorkspace,
                primaryProviderId = agent.ModelPolicy.PrimaryProviderProfileId.Value,
                dataLocality = agent.ModelPolicy.DataLocality.ToString(),
                agent.ModelPolicy.AllowFallback,
                memoryScope = agent.MemoryPolicy.Scope.ToString(),
                agent.MemoryPolicy.RetentionDays,
                networkPosture = agent.CapabilityPolicy.NetworkPosture.ToString(),
                toolGrants = agent.CapabilityPolicy.ToolGrants,
                skillGrants = agent.CapabilityPolicy.SkillGrants,
                budget = agent.Budget,
                childLimits = agent.ChildLimits,
                learningMode = agent.LearningPolicy.Mode.ToString(),
                mutableSkillScope = agent.LearningPolicy.MutableSkillScope.ToString(),
                agent.Version,
                agent.UpdatedAt,
            }),
            correlationId = context.TraceIdentifier,
        });
    }

    private static async Task<IResult> GetRunOptionsAsync(
        Guid agentId,
        HttpContext context,
        ReadyAdminSessionManager sessions,
        IInstallationStateReader stateReader,
        IAgentIdentityRepository agents,
        IProviderProfileRepository providers,
        ISkillRegistryRepository skills,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var acquired = await AcquireAsync(
            context, sessions, stateReader, clock, requireCsrf: false, cancellationToken);
        if (acquired.Failure is not null)
        {
            return acquired.Failure;
        }

        var agent = await agents.FindByIdAsync(new AgentIdentityId(agentId), cancellationToken);
        if (agent is null || agent.InstallationId != acquired.Session!.InstallationId)
        {
            return Problem(context, 404, "Agent not found",
                "The selected agent does not belong to this installation.", "not-found");
        }

        var provider = await providers.FindByIdAsync(
            agent.ModelPolicy.PrimaryProviderProfileId, cancellationToken);
        if (provider is null || provider.InstallationId != acquired.Session.InstallationId)
        {
            return Problem(context, 409, "Provider unavailable",
                "The agent's pinned provider profile is missing or outside this installation.",
                "provider-unavailable");
        }

        var registered = await skills.ListAsync(acquired.Session.InstallationId, cancellationToken);
        return Results.Ok(new
        {
            agent = new
            {
                id = agent.Id.Value,
                agent.Name,
                agent.Version,
                systemInstruction = BuildSystemInstruction(agent, null, []),
            },
            provider = new
            {
                id = provider.Id.Value,
                provider.Name,
                provider.ProviderType,
                endpoint = provider.Endpoint.ToString(),
                provider.Model,
            },
            responseDepths = new[]
            {
                ResponseDepthOption("concise", "Concise", 384, agent.Budget.MaxOutputTokens),
                ResponseDepthOption("balanced", "Balanced", 1_024, agent.Budget.MaxOutputTokens),
                ResponseDepthOption("detailed", "Detailed", 2_048, agent.Budget.MaxOutputTokens),
            },
            skills = registered
                .Where(item => item.Status is not (SkillPackageStatus.Archived or SkillPackageStatus.Quarantined))
                .OrderBy(item => item.Package.Id.Value, StringComparer.Ordinal)
                .ThenByDescending(item => item.Package.Version.Value, StringComparer.Ordinal)
                .Select(item => new
                {
                    id = item.Package.Id.Value,
                    version = item.Package.Version.Value,
                    item.Package.Description,
                    status = item.Status.ToString(),
                    granted = agent.CapabilityPolicy.SkillGrants.Contains(
                        item.Package.Id.Value, StringComparer.Ordinal),
                    selectable = item.Status is SkillPackageStatus.Active &&
                        agent.CapabilityPolicy.SkillGrants.Contains(item.Package.Id.Value, StringComparer.Ordinal),
                    permissions = item.Package.Permissions,
                }),
            restrictions = new
            {
                modelRoute = "Pinned local/private provider only",
                tools = "Denied",
                browsing = "Denied",
                memory = "Not attached",
                files = "Denied",
                messaging = "Denied",
                devices = "Denied",
                fallback = "Denied",
            },
            correlationId = context.TraceIdentifier,
        });
    }

    private static async Task<IResult> ListRunsAsync(
        HttpContext context,
        ReadyAdminSessionManager sessions,
        IInstallationStateReader stateReader,
        ITaskSnapshotStore snapshots,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var acquired = await AcquireAsync(
            context, sessions, stateReader, clock, requireCsrf: false, cancellationToken);
        if (acquired.Failure is not null)
        {
            return acquired.Failure;
        }

        var items = await snapshots.ListLatestAsync(
            acquired.Session!.InstallationId, 100, cancellationToken);
        return Results.Ok(new
        {
            runs = items.Select(item => RunResponse(item)),
            correlationId = context.TraceIdentifier,
        });
    }

    private static async Task<IResult> CreateRunAsync(
        HttpContext context,
        CreateMvpRunRequest request,
        ReadyAdminSessionManager sessions,
        IInstallationStateReader stateReader,
        IAgentIdentityRepository agents,
        ITaskOrchestrator orchestrator,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var acquired = await AcquireMutationAsync(
            context, sessions, stateReader, clock, cancellationToken);
        if (acquired.Failure is not null)
        {
            return acquired.Failure;
        }

        if (request is null || request.AgentId == Guid.Empty || !Text(request.Name, 256))
        {
            return Problem(context, 400, "Invalid run",
                "Choose an agent and enter a run name of at most 256 printable characters.", "validation-failure");
        }

        var agent = await agents.FindByIdAsync(new AgentIdentityId(request.AgentId), cancellationToken);
        if (agent is null || agent.InstallationId != acquired.Session!.InstallationId)
        {
            return Problem(context, 404, "Agent not found",
                "The selected agent does not belong to this installation.", "not-found");
        }

        var idempotencyKey = context.Request.Headers["Idempotency-Key"].ToString();
        var stableRequestIdentity = StableRequestIdentity(
            acquired.Session.InstallationId, "planned-run", idempotencyKey);
        var definition = new OrchestrationTaskDefinition(
            new OrchestrationTaskId(new Guid(stableRequestIdentity.AsSpan(0, 16))),
            acquired.Session.InstallationId,
            agent.Id,
            agent.Version,
            OrchestrationPattern.Sequential,
            [new TaskNodeDefinition(
                new TaskNodeId("objective"),
                request.Name.Trim(),
                [],
                [],
                [],
                new TaskExecutionBudget(
                    agent.Budget.MaxToolInvocations,
                    agent.Budget.MaxInputTokens,
                    Math.Max(1, agent.Budget.MaxOutputTokens),
                    Math.Max(1, agent.Budget.MaxWallClockSeconds)),
                new TaskRetryPolicy(1, 0))],
            1,
            agent.ChildLimits.MaxDepth,
            agent.ChildLimits.MaxChildren,
            SnapshotHash(new { agent.CapabilityPolicy, agent.Version }),
            SnapshotHash(new { agent.Budget, agent.ChildLimits, agent.Version }),
            SnapshotHash(new { agent.CapabilityPolicy.SkillGrants, agent.Version }));
        var result = await orchestrator.CreateAsync(
            definition,
            acquired.Session.ActorId,
            StoredIdempotencyKey("planned-run", idempotencyKey),
            new CorrelationId($"admin-run:{Convert.ToHexStringLower(stableRequestIdentity)}"),
            null,
            cancellationToken);
        if (!result.IsSuccess)
        {
            return DomainProblem(context, result.Failure!, "Run creation failed");
        }

        if (result.Value.WasReplay)
        {
            context.Response.Headers["Idempotent-Replay"] = "true";
        }
        return Results.Created(
            $"/api/v1/admin/runs/{result.Value.Snapshot.Definition.Id.Value:D}",
            RunResponse(result.Value.Snapshot, result.Value.WasReplay));
    }

    private static async Task<IResult> TestAgentChatAsync(
        Guid agentId,
        HttpContext context,
        TestAgentChatRequest request,
        ReadyAdminSessionManager sessions,
        IInstallationStateReader stateReader,
        IAgentIdentityRepository agents,
        IProviderProfileRepository providers,
        ITaskOrchestrator orchestrator,
        ILocalModelInteractionService interactions,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var acquired = await AcquireMutationAsync(
            context, sessions, stateReader, clock, cancellationToken);
        if (acquired.Failure is not null)
        {
            return acquired.Failure;
        }
        if (agentId == Guid.Empty || request is null || !PromptText(request.Prompt, 16_384))
        {
            return Problem(context, 400, "Invalid prompt",
                "Choose an agent and enter a prompt of at most 16,384 printable characters.", "validation-failure");
        }

        var session = acquired.Session!;
        var idempotencyKey = context.Request.Headers["Idempotency-Key"].ToString();
        var scopedKey = $"agent-chat:{agentId:D}:{idempotencyKey}";
        var requestHash = SnapshotHash(new { agentId, request.Prompt });
        await session.MutationGate.WaitAsync(cancellationToken);
        try
        {
            if (session.Results.TryGetValue(scopedKey, out var existing))
            {
                return string.Equals(existing.RequestHash, requestHash, StringComparison.Ordinal)
                    ? Replay(context, existing.Response)
                    : Problem(context, 409, "Idempotency conflict",
                        "The idempotency key is already bound to a different prompt.", "idempotency-conflict");
            }

            var agent = await agents.FindByIdAsync(new AgentIdentityId(agentId), cancellationToken);
            if (agent is null || agent.InstallationId != session.InstallationId)
            {
                return Problem(context, 404, "Agent not found",
                    "The selected agent does not belong to this installation.", "not-found");
            }
            if (agent.ModelPolicy.DataLocality is not ModelDataLocality.LocalOnly ||
                agent.ModelPolicy.AllowFallback || agent.Budget.MaxToolInvocations != 0)
            {
                return Problem(context, 403, "Agent policy denied",
                    "Interactive MVP testing requires a local-only, no-fallback agent with a zero tool budget.", "policy-denied");
            }

            var provider = await providers.FindByIdAsync(
                agent.ModelPolicy.PrimaryProviderProfileId, cancellationToken);
            if (provider is null || provider.InstallationId != session.InstallationId)
            {
                return Problem(context, 409, "Provider unavailable",
                    "The agent's pinned provider profile is missing or outside this installation.", "provider-unavailable");
            }

            var stableRequestIdentity = StableRequestIdentity(
                session.InstallationId, $"agent-chat:{agentId:D}", idempotencyKey);
            var taskId = new OrchestrationTaskId(new Guid(stableRequestIdentity.AsSpan(0, 16)));
            var correlation = new CorrelationId($"admin-chat:{Convert.ToHexStringLower(stableRequestIdentity)}");
            var maximumOutputTokens = (int)Math.Clamp(agent.Budget.MaxOutputTokens, 1L, 2_048L);
            var maximumWallClockSeconds = Math.Clamp(agent.Budget.MaxWallClockSeconds, 1, 120);
            var definition = new OrchestrationTaskDefinition(
                taskId,
                session.InstallationId,
                agent.Id,
                agent.Version,
                OrchestrationPattern.Sequential,
                [new TaskNodeDefinition(
                    new TaskNodeId("local-model"),
                    "Interactive local model test",
                    [],
                    [],
                    [],
                    new TaskExecutionBudget(
                        0,
                        agent.Budget.MaxInputTokens,
                        maximumOutputTokens,
                        maximumWallClockSeconds),
                    new TaskRetryPolicy(1, 0))],
                1,
                0,
                0,
                SnapshotHash(new { agent.CapabilityPolicy, agent.Version }),
                SnapshotHash(new { agent.Budget, agent.Version, InteractiveMaximumOutputTokens = maximumOutputTokens }),
                SnapshotHash(new { agent.CapabilityPolicy.SkillGrants, agent.Version }));
            var created = await orchestrator.CreateAsync(
                definition,
                session.ActorId,
                StoredIdempotencyKey("agent-chat", idempotencyKey),
                correlation,
                null,
                cancellationToken);
            if (!created.IsSuccess)
            {
                return DomainProblem(context, created.Failure!, "Agent test failed");
            }
            if (created.Value.WasReplay && created.Value.Snapshot.State is not OrchestrationTaskState.Planned)
            {
                return Problem(context, 409, "Prior test cannot be replayed",
                    "The durable test already advanced, but its transient response is no longer in this operator session.",
                    "response-not-retained");
            }

            const string owner = "ready-ui:local-model-test";
            var claimed = await orchestrator.ClaimAsync(
                taskId,
                created.Value.Snapshot.Version,
                new TaskNodeId("local-model"),
                owner,
                TimeSpan.FromMinutes(2),
                cancellationToken);
            if (!claimed.IsSuccess)
            {
                return DomainProblem(context, claimed.Failure!, "Agent test could not start");
            }

            var systemInstruction = BuildSystemInstruction(agent);
            DomainResult<LocalModelInteractionResult> interaction;
            try
            {
                interaction = await interactions.InvokeAsync(new LocalModelInteractionRequest(
                    new ModelRequestId(taskId.Value),
                    provider,
                    systemInstruction,
                    request.Prompt,
                    new ModelInvocationLimits(
                        maximumOutputTokens,
                        0,
                        4_096,
                        maximumWallClockSeconds),
                    correlation), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                await orchestrator.FailAsync(
                    taskId,
                    claimed.Value.Snapshot.Version,
                    new TaskNodeId("local-model"),
                    owner,
                    claimed.Value.LeaseToken,
                    OrchestrationTaskStateMachine.EmptyHash,
                    FailureCode.RecoverableExternalFailure,
                    retryable: false,
                    CancellationToken.None);
                throw;
            }

            if (!interaction.IsSuccess)
            {
                var failureEvidence = SnapshotHash(new
                {
                    taskId = taskId.Value,
                    code = interaction.Failure!.Code.ToString(),
                    interaction.Failure.IsRetryable,
                });
                await orchestrator.FailAsync(
                    taskId,
                    claimed.Value.Snapshot.Version,
                    new TaskNodeId("local-model"),
                    owner,
                    claimed.Value.LeaseToken,
                    failureEvidence,
                    interaction.Failure.Code,
                    interaction.Failure.IsRetryable,
                    CancellationToken.None);
                return DomainProblem(context, interaction.Failure, "Local model test failed");
            }

            var completed = await orchestrator.CompleteAsync(
                taskId,
                claimed.Value.Snapshot.Version,
                new TaskNodeId("local-model"),
                owner,
                claimed.Value.LeaseToken,
                interaction.Value.EvidenceHash,
                CancellationToken.None);
            if (!completed.IsSuccess)
            {
                return DomainProblem(context, completed.Failure!, "Agent test completion failed");
            }

            var response = new
            {
                requestId = interaction.Value.RequestId.Value,
                output = interaction.Value.Text,
                usage = interaction.Value.Usage,
                finishReason = interaction.Value.FinishReason.ToString(),
                interaction.Value.ContextRedactionCount,
                interaction.Value.EventCount,
                interaction.Value.EvidenceHash,
                provider = new
                {
                    id = provider.Id.Value,
                    provider.Name,
                    provider.ProviderType,
                    endpoint = provider.Endpoint.ToString(),
                    provider.Model,
                },
                run = RunResponse(completed.Value.Snapshot),
                correlationId = correlation.Value,
            };
            RetainBoundedSessionResults(session.Results, 31);
            session.Results[scopedKey] = new ReadyAdminIdempotencyResult(requestHash, response);
            return Results.Ok(response);
        }
        finally
        {
            session.MutationGate.Release();
        }
    }

    private static async Task StreamAgentChatAsync(
        Guid agentId,
        HttpContext context,
        StreamAgentChatRequest request,
        ReadyAdminSessionManager sessions,
        IInstallationStateReader stateReader,
        IAgentIdentityRepository agents,
        IProviderProfileRepository providers,
        ITaskOrchestrator orchestrator,
        ITaskSnapshotStore snapshots,
        ISkillSnapshotService skillSnapshots,
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
        if (agentId == Guid.Empty || request is null || !ValidStreamRequest(request))
        {
            await Problem(context, 400, "Invalid run configuration",
                "Choose an agent, use bounded printable run fields, select at most four distinct skill IDs, and choose concise, balanced, or detailed output.",
                "validation-failure").ExecuteAsync(context);
            return;
        }

        var session = acquired.Session!;
        var idempotencyKey = context.Request.Headers["Idempotency-Key"].ToString();
        AgentIdentity? agent = null;
        ProviderProfile? provider = null;
        TaskLeaseGrant? claimed = null;
        OrchestrationTaskId taskId = default;
        CorrelationId correlation = default;
        var systemInstruction = string.Empty;
        var runName = string.IsNullOrWhiteSpace(request.Name) ? "Local model run" : request.Name.Trim();
        var responseDepth = string.IsNullOrWhiteSpace(request.ResponseDepth)
            ? "balanced"
            : request.ResponseDepth.Trim().ToLowerInvariant();
        var runInstructions = string.IsNullOrWhiteSpace(request.RunInstructions)
            ? null
            : request.RunInstructions.Trim();
        var selectedSkillIds = (request.SkillIds ?? [])
            .Select(item => item.Trim())
            .Order(StringComparer.Ordinal)
            .ToArray();
        string[] appliedSkillIds = [];
        var skillSnapshotHash = SnapshotHash(new { agentId, SelectedSkillIds = Array.Empty<string>() });
        var maximumOutputTokens = 0;
        var maximumWallClockSeconds = 0;
        await session.MutationGate.WaitAsync(cancellationToken);
        try
        {
            agent = await agents.FindByIdAsync(new AgentIdentityId(agentId), cancellationToken);
            if (agent is null || agent.InstallationId != session.InstallationId)
            {
                await Problem(context, 404, "Agent not found",
                    "The selected agent does not belong to this installation.", "not-found").ExecuteAsync(context);
                return;
            }
            if (agent.ModelPolicy.DataLocality is not ModelDataLocality.LocalOnly ||
                agent.ModelPolicy.AllowFallback || agent.Budget.MaxToolInvocations != 0)
            {
                await Problem(context, 403, "Agent policy denied",
                    "Interactive MVP testing requires a local-only, no-fallback agent with a zero tool budget.",
                    "policy-denied").ExecuteAsync(context);
                return;
            }

            provider = await providers.FindByIdAsync(
                agent.ModelPolicy.PrimaryProviderProfileId, cancellationToken);
            if (provider is null || provider.InstallationId != session.InstallationId)
            {
                await Problem(context, 409, "Provider unavailable",
                    "The agent's pinned provider profile is missing or outside this installation.",
                    "provider-unavailable").ExecuteAsync(context);
                return;
            }

            var stableRequestIdentity = StableRequestIdentity(
                session.InstallationId, $"agent-chat-stream:{agentId:D}", idempotencyKey);
            taskId = new OrchestrationTaskId(new Guid(stableRequestIdentity.AsSpan(0, 16)));
            correlation = new CorrelationId(
                $"admin-chat-stream:{Convert.ToHexStringLower(stableRequestIdentity)}");
            maximumOutputTokens = ResponseTokenLimit(responseDepth, agent.Budget.MaxOutputTokens);
            maximumWallClockSeconds = Math.Clamp(agent.Budget.MaxWallClockSeconds, 1, 120);

            if (selectedSkillIds.Any(skillId => !agent.CapabilityPolicy.SkillGrants.Contains(
                skillId, StringComparer.Ordinal)))
            {
                await Problem(context, 403, "Skill policy denied",
                    "Every selected skill must already be granted to this exact agent version.",
                    "policy-denied").ExecuteAsync(context);
                return;
            }

            var skillBodies = new List<RunSkillBody>();
            if (selectedSkillIds.Length > 0)
            {
                var skillRequestIdentity = SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
                {
                    StableIdentity = Convert.ToHexStringLower(stableRequestIdentity),
                    request.Prompt,
                    Name = runName,
                    RunInstructions = runInstructions,
                    ResponseDepth = responseDepth,
                    SkillIds = selectedSkillIds,
                }, HashJson));
                var skillSnapshot = await skillSnapshots.CreateAsync(
                    new SkillRunSnapshotId(new Guid(skillRequestIdentity.AsSpan(0, 16))),
                    session.InstallationId,
                    selectedSkillIds.Select(item => new SkillId(item)).ToArray(),
                    session.ActorId,
                    StoredIdempotencyKey("agent-chat-stream-skills", idempotencyKey),
                    correlation,
                    null,
                    cancellationToken);
                if (!skillSnapshot.IsSuccess)
                {
                    await DomainProblem(context, skillSnapshot.Failure!, "Run skill snapshot failed")
                        .ExecuteAsync(context);
                    return;
                }

                appliedSkillIds = skillSnapshot.Value.Selections
                    .Select(item => item.SkillId.Value)
                    .Order(StringComparer.Ordinal)
                    .ToArray();
                if (appliedSkillIds.Any(skillId => !agent.CapabilityPolicy.SkillGrants.Contains(
                    skillId, StringComparer.Ordinal)))
                {
                    await Problem(context, 403, "Skill dependency policy denied",
                        "A selected skill depends on another skill that is not granted to this agent.",
                        "policy-denied").ExecuteAsync(context);
                    return;
                }

                foreach (var selection in skillSnapshot.Value.Selections.OrderBy(item => item.SkillId.Value, StringComparer.Ordinal))
                {
                    var body = await skillSnapshots.OpenBodyAsync(
                        skillSnapshot.Value.Id, selection.SkillId, cancellationToken);
                    if (!body.IsSuccess)
                    {
                        await DomainProblem(context, body.Failure!, "Run skill content failed integrity validation")
                            .ExecuteAsync(context);
                        return;
                    }
                    skillBodies.Add(new RunSkillBody(
                        selection.SkillId.Value,
                        selection.Version.Value,
                        selection.PackageHash,
                        body.Value));
                }
                skillSnapshotHash = skillSnapshot.Value.SnapshotHash;
            }

            systemInstruction = BuildSystemInstruction(agent, runInstructions, skillBodies);
            if (systemInstruction.Length > 24_576)
            {
                await Problem(context, 422, "Run context too large",
                    "The approved system and skill context exceeds the interactive run bound.",
                    "budget-exceeded").ExecuteAsync(context);
                return;
            }

            var definition = new OrchestrationTaskDefinition(
                taskId,
                session.InstallationId,
                agent.Id,
                agent.Version,
                OrchestrationPattern.Sequential,
                [new TaskNodeDefinition(
                    new TaskNodeId("local-model"),
                    runName,
                    [],
                    [],
                    [],
                    new TaskExecutionBudget(
                        0,
                        agent.Budget.MaxInputTokens,
                        maximumOutputTokens,
                        maximumWallClockSeconds),
                    new TaskRetryPolicy(1, 0))],
                1,
                0,
                0,
                SnapshotHash(new { agent.CapabilityPolicy, agent.Version }),
                SnapshotHash(new
                {
                    agent.Budget,
                    agent.Version,
                    InteractiveMaximumOutputTokens = maximumOutputTokens,
                    ResponseDepth = responseDepth,
                    RunInstructionsHash = runInstructions is null ? null : SnapshotHash(runInstructions),
                    Streaming = true,
                }),
                skillSnapshotHash);
            var created = await orchestrator.CreateAsync(
                definition,
                session.ActorId,
                StoredIdempotencyKey("agent-chat-stream", idempotencyKey),
                correlation,
                null,
                cancellationToken);
            if (!created.IsSuccess)
            {
                await DomainProblem(context, created.Failure!, "Streaming agent test failed").ExecuteAsync(context);
                return;
            }
            if (created.Value.WasReplay)
            {
                await Problem(context, 409, "Stream cannot be replayed",
                    "A transient response stream requires a fresh idempotency key.",
                    "stream-replay-denied").ExecuteAsync(context);
                return;
            }

            const string owner = "ready-ui:streaming-local-model-test";
            var claim = await orchestrator.ClaimAsync(
                taskId,
                created.Value.Snapshot.Version,
                new TaskNodeId("local-model"),
                owner,
                TimeSpan.FromMinutes(2),
                cancellationToken);
            if (!claim.IsSuccess)
            {
                await DomainProblem(context, claim.Failure!, "Streaming agent test could not start")
                    .ExecuteAsync(context);
                return;
            }
            claimed = claim.Value;
        }
        finally
        {
            session.MutationGate.Release();
        }

        var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var active = new ReadyActiveInteraction(
            taskId, session.InstallationId, session.Hash, linkedCancellation);
        if (!activeInteractions.TryAdd(active))
        {
            linkedCancellation.Dispose();
            await orchestrator.FailAsync(
                taskId,
                claimed!.Snapshot.Version,
                new TaskNodeId("local-model"),
                "ready-ui:streaming-local-model-test",
                claimed.LeaseToken,
                OrchestrationTaskStateMachine.EmptyHash,
                FailureCode.ConcurrencyConflict,
                retryable: false,
                CancellationToken.None);
            await Problem(context, 409, "Interaction already active",
                "Only one active stream may own this durable run.", "concurrency-conflict").ExecuteAsync(context);
            return;
        }

        const string streamOwner = "ready-ui:streaming-local-model-test";
        try
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "text/event-stream; charset=utf-8";
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["X-Accel-Buffering"] = "no";
            context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
            await context.Response.StartAsync(context.RequestAborted);
            await WriteSseAsync(context, "run-started", new
            {
                taskId = taskId.Value,
                state = claimed!.Snapshot.State.ToString(),
                configuration = new
                {
                    name = runName,
                    responseDepth,
                    maximumOutputTokens,
                    skillIds = appliedSkillIds,
                    hasRunInstructions = runInstructions is not null,
                },
                provider = new
                {
                    id = provider!.Id.Value,
                    provider.Name,
                    provider.ProviderType,
                    endpoint = provider.Endpoint.ToString(),
                    provider.Model,
                },
                correlationId = correlation.Value,
            }, context.RequestAborted);

            var observer = new SseInteractionObserver(context);
            var interaction = await interactions.InvokeAsync(new LocalModelInteractionRequest(
                new ModelRequestId(taskId.Value),
                provider,
                systemInstruction,
                request.Prompt,
                new ModelInvocationLimits(
                    maximumOutputTokens,
                    0,
                    4_096,
                    maximumWallClockSeconds),
                correlation), observer, linkedCancellation.Token);
            if (!interaction.IsSuccess)
            {
                var failureEvidence = SnapshotHash(new
                {
                    taskId = taskId.Value,
                    code = interaction.Failure!.Code.ToString(),
                    interaction.Failure.IsRetryable,
                });
                var failed = await orchestrator.FailAsync(
                    taskId,
                    claimed.Snapshot.Version,
                    new TaskNodeId("local-model"),
                    streamOwner,
                    claimed.LeaseToken,
                    failureEvidence,
                    interaction.Failure.Code,
                    interaction.Failure.IsRetryable,
                    CancellationToken.None);
                if (!failed.IsSuccess && await WasDurablyCanceledAsync(snapshots, taskId))
                {
                    await WriteSseAsync(context, "canceled", new { taskId = taskId.Value },
                        context.RequestAborted);
                    return;
                }
                await WriteSseAsync(context, "failed", new
                {
                    code = interaction.Failure.Code.ToString(),
                    interaction.Failure.Message,
                    run = failed.IsSuccess ? RunResponse(failed.Value.Snapshot) : null,
                }, context.RequestAborted);
                return;
            }

            var completed = await orchestrator.CompleteAsync(
                taskId,
                claimed.Snapshot.Version,
                new TaskNodeId("local-model"),
                streamOwner,
                claimed.LeaseToken,
                interaction.Value.EvidenceHash,
                CancellationToken.None);
            if (!completed.IsSuccess)
            {
                if (activeInteractions.WasCanceled(taskId) ||
                    await WasDurablyCanceledAsync(snapshots, taskId))
                {
                    await WriteSseAsync(context, "canceled", new { taskId = taskId.Value },
                        context.RequestAborted);
                    return;
                }
                await WriteSseAsync(context, "failed", new
                {
                    code = completed.Failure!.Code.ToString(),
                    completed.Failure.Message,
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
                run = RunResponse(completed.Value.Snapshot),
            }, context.RequestAborted);
        }
        catch (OperationCanceledException) when (activeInteractions.WasCanceled(taskId))
        {
            await WriteSseAsync(context, "canceled", new { taskId = taskId.Value },
                context.RequestAborted);
        }
        catch (OperationCanceledException)
        {
            await orchestrator.FailAsync(
                taskId,
                claimed.Snapshot.Version,
                new TaskNodeId("local-model"),
                streamOwner,
                claimed.LeaseToken,
                OrchestrationTaskStateMachine.EmptyHash,
                FailureCode.RecoverableExternalFailure,
                retryable: false,
                CancellationToken.None);
        }
        finally
        {
            activeInteractions.Remove(taskId);
        }
    }

    private static async Task<IResult> CancelRunAsync(
        Guid taskId,
        HttpContext context,
        ReadyAdminSessionManager sessions,
        IInstallationStateReader stateReader,
        ITaskSnapshotStore snapshots,
        ITaskOrchestrator orchestrator,
        ReadyActiveInteractionRegistry activeInteractions,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var acquired = await AcquireMutationAsync(
            context, sessions, stateReader, clock, cancellationToken);
        if (acquired.Failure is not null)
        {
            return acquired.Failure;
        }

        var current = await snapshots.FindLatestAsync(new OrchestrationTaskId(taskId), cancellationToken);
        if (current is null || current.Definition.InstallationId != acquired.Session!.InstallationId)
        {
            return Problem(context, 404, "Run not found",
                "No run exists under this installation.", "not-found");
        }
        if (current.State is OrchestrationTaskState.Canceled)
        {
            activeInteractions.TryCancel(
                current.Definition.Id, acquired.Session.InstallationId, acquired.Session.Hash);
            context.Response.Headers["Idempotent-Replay"] = "true";
            return Results.Ok(RunResponse(current, true));
        }

        var result = await orchestrator.CancelAsync(
            current.Definition.Id, current.Version, cancellationToken);
        if (result.IsSuccess)
        {
            activeInteractions.TryCancel(
                current.Definition.Id, acquired.Session.InstallationId, acquired.Session.Hash);
        }
        return result.IsSuccess
            ? Results.Ok(RunResponse(result.Value.Snapshot, result.Value.WasReplay))
            : DomainProblem(context, result.Failure!, "Run cancellation failed");
    }

    private static async Task<IResult> ListSkillsAsync(
        HttpContext context,
        ReadyAdminSessionManager sessions,
        IInstallationStateReader stateReader,
        ISkillRegistryRepository skills,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var acquired = await AcquireAsync(
            context, sessions, stateReader, clock, requireCsrf: false, cancellationToken);
        if (acquired.Failure is not null)
        {
            return acquired.Failure;
        }

        var items = await skills.ListAsync(acquired.Session!.InstallationId, cancellationToken);
        return Results.Ok(new
        {
            skills = items.Select(SkillResponse),
            seedAvailable = Directory.Exists(SeedSkillDirectory()),
            correlationId = context.TraceIdentifier,
        });
    }

    private static async Task<IResult> InstallSeedSkillAsync(
        HttpContext context,
        ReadyAdminSessionManager sessions,
        IInstallationStateReader stateReader,
        ISkillRegistryService skills,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var acquired = await AcquireMutationAsync(
            context, sessions, stateReader, clock, cancellationToken);
        if (acquired.Failure is not null)
        {
            return acquired.Failure;
        }

        var directory = SeedSkillDirectory();
        if (!Directory.Exists(directory))
        {
            return Problem(context, 503, "Seed package unavailable",
                "The packaged C# review skill was not found in this installation.", "seed-unavailable");
        }

        var result = await skills.InstallAsync(
            acquired.Session!.InstallationId,
            directory,
            SkillPackageProvenance.Seed,
            acquired.Session.ActorId,
            new CorrelationId(context.TraceIdentifier),
            cancellationToken);
        if (!result.IsSuccess)
        {
            return DomainProblem(context, result.Failure!, "Skill installation failed");
        }
        if (result.Value.WasReplay)
        {
            context.Response.Headers["Idempotent-Replay"] = "true";
        }
        return Results.Ok(new
        {
            skill = SkillResponse(result.Value.Version),
            wasReplay = result.Value.WasReplay,
            correlationId = context.TraceIdentifier,
        });
    }

    private static async Task<IResult> ListLearningSignalsAsync(
        HttpContext context,
        ReadyAdminSessionManager sessions,
        IInstallationStateReader stateReader,
        ILearningRepository learning,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var acquired = await AcquireAsync(
            context, sessions, stateReader, clock, requireCsrf: false, cancellationToken);
        if (acquired.Failure is not null)
        {
            return acquired.Failure;
        }

        var items = await learning.ListSignalsAsync(
            acquired.Session!.InstallationId, 100, cancellationToken);
        return Results.Ok(new
        {
            signals = items.Select(item => LearningSignalResponse(item.Signal, item.Classification)),
            correlationId = context.TraceIdentifier,
        });
    }

    private static async Task<IResult> CaptureLearningSignalAsync(
        HttpContext context,
        CaptureLearningSignalWebRequest request,
        ReadyAdminSessionManager sessions,
        IInstallationStateReader stateReader,
        ITaskSnapshotStore tasks,
        ILearningRepository learningRepository,
        ILearningGovernanceService learning,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var acquired = await AcquireMutationAsync(
            context, sessions, stateReader, clock, cancellationToken);
        if (acquired.Failure is not null)
        {
            return acquired.Failure;
        }

        var summary = NormalizeLearningSummary(request?.Summary);
        if (request is null || request.SourceTaskId == Guid.Empty ||
            !Enum.TryParse<LearningSignalKind>(request.Kind, ignoreCase: true, out var kind) ||
            kind is LearningSignalKind.RepeatedSkillChain || summary is null ||
            request.OccurrenceCount is < 1 or > 1_000_000)
        {
            return Problem(context, 400, "Invalid learning evidence",
                "Choose a durable source run, a supported evidence kind, a one-line redacted summary, and a bounded occurrence count.",
                "validation-failure");
        }

        var session = acquired.Session!;
        await session.MutationGate.WaitAsync(cancellationToken);
        try
        {
            var task = await tasks.FindLatestAsync(
                new OrchestrationTaskId(request.SourceTaskId), cancellationToken);
            if (task is null || task.Definition.InstallationId != session.InstallationId)
            {
                return Problem(context, 404, "Source run not found",
                    "Learning evidence must bind to a durable run from this installation.", "not-found");
            }
            if (task.State is not (OrchestrationTaskState.Completed or OrchestrationTaskState.Failed or
                OrchestrationTaskState.Canceled or OrchestrationTaskState.DeadLettered))
            {
                return Problem(context, 409, "Source run is not terminal",
                    "Wait for a durable terminal receipt before capturing learning evidence.",
                    "concurrency-conflict");
            }

            var idempotencyKey = context.Request.Headers["Idempotency-Key"].ToString();
            var stableIdentity = StableRequestIdentity(
                session.InstallationId, "learning-signal", idempotencyKey);
            var signalId = new LearningSignalId(new Guid(stableIdentity.AsSpan(0, 16)));
            var correlation = new CorrelationId(
                $"admin-learning:{Convert.ToHexStringLower(stableIdentity)}");
            var causation = new CorrelationId($"run:{request.SourceTaskId:D}");
            var existing = await learningRepository.FindSignalAsync(signalId, cancellationToken);
            if (existing is not null)
            {
                var sameRequest = existing.Value.Signal.InstallationId == session.InstallationId &&
                    existing.Value.Signal.Kind == kind &&
                    existing.Value.Signal.RedactedSummary == summary &&
                    existing.Value.Signal.SourceEvidenceHash == task.SnapshotHash &&
                    existing.Value.Signal.OccurrenceCount == request.OccurrenceCount &&
                    existing.Value.Signal.CapturedBy == session.ActorId &&
                    existing.Value.Signal.CausationId == causation;
                return sameRequest
                    ? Replay(context, LearningSignalResponse(existing.Value.Signal, existing.Value.Classification))
                    : Problem(context, 409, "Learning evidence conflict",
                        "The idempotency key is already bound to different learning evidence.",
                        "concurrency-conflict");
            }

            var captured = await learning.CaptureAsync(new CaptureLearningSignalRequest(
                signalId,
                session.InstallationId,
                kind,
                summary,
                task.SnapshotHash,
                [],
                [],
                [],
                request.OccurrenceCount,
                session.ActorId,
                correlation,
                causation), cancellationToken);
            return captured.IsSuccess
                ? Results.Created(
                    $"/api/v1/admin/learning/signals/{signalId.Value:D}",
                    LearningSignalResponse(
                        (await learningRepository.FindSignalAsync(signalId, cancellationToken))!.Value.Signal,
                        captured.Value))
                : DomainProblem(context, captured.Failure!, "Learning evidence was not accepted");
        }
        finally
        {
            session.MutationGate.Release();
        }
    }

    private static async Task<ReadyAdminAcquisition> AcquireMutationAsync(
        HttpContext context,
        ReadyAdminSessionManager sessions,
        IInstallationStateReader stateReader,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var acquired = await AcquireAsync(
            context, sessions, stateReader, clock, requireCsrf: true, cancellationToken);
        if (acquired.Failure is not null)
        {
            return acquired;
        }

        var key = context.Request.Headers["Idempotency-Key"].ToString();
        return Text(key, 256) && key.All(character =>
                char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-' or ':')
            ? acquired
            : new ReadyAdminAcquisition(null, Problem(context, 400, "Idempotency required",
                "A bounded Idempotency-Key is required for every mutation.", "idempotency-required"));
    }

    private static async Task<ReadyAdminAcquisition> AcquireAsync(
        HttpContext context,
        ReadyAdminSessionManager sessions,
        IInstallationStateReader stateReader,
        IClock clock,
        bool requireCsrf,
        CancellationToken cancellationToken)
    {
        if (!IsTrustedWorkspaceRequest(context))
        {
            return new(null, Problem(context, 403, "Protected workspace required",
                "The operator workspace requires loopback or the explicitly enabled HTTPS remote origin.",
                "workspace-origin-required"));
        }

        var session = sessions.Validate(
            context.Request.Cookies[ReadyAdminSessionManager.CookieName],
            context.Request.Headers["X-CSRF-Token"].ToString(),
            clock.UtcNow,
            requireCsrf);
        if (session is null)
        {
            return new(null, Problem(context, 401, "Operator session required",
                "Open a fresh protected local operator session.", "session-required"));
        }

        var state = await stateReader.ReadAsync(cancellationToken);
        return state.IsReady && state.Id == session.InstallationId
            ? new ReadyAdminAcquisition(session, null)
            : new ReadyAdminAcquisition(null, Problem(context, 409, "Installation changed",
                "The operator session no longer matches the current Ready installation.", "session-stale"));
    }

    private static object SessionResponse(ReadyAdminSession session) => new
    {
        csrfToken = session.CsrfToken,
        installationId = session.InstallationId.Value,
        actorId = session.ActorId.Value,
        session.ExpiresAtUtc,
    };

    private static object RunResponse(OrchestrationTaskSnapshot snapshot, bool replay = false) => new
    {
        taskId = snapshot.Definition.Id.Value,
        agentId = snapshot.Definition.AgentId.Value,
        agentVersion = snapshot.Definition.AgentVersion,
        name = snapshot.Definition.Nodes[0].Name,
        pattern = snapshot.Definition.Pattern.ToString(),
        state = snapshot.State.ToString(),
        nodes = snapshot.Nodes.Select(node => new
        {
            id = node.Definition.Id.Value,
            node.Definition.Name,
            state = node.State.ToString(),
            node.Attempt,
            failureCode = node.FailureCode?.ToString(),
        }),
        snapshot.Version,
        snapshot.SnapshotHash,
        snapshot.CreatedAt,
        snapshot.UpdatedAt,
        wasReplay = replay,
    };

    private static object SkillResponse(RegisteredSkillVersion skill) => new
    {
        id = skill.Package.Id.Value,
        version = skill.Package.Version.Value,
        skill.Package.Description,
        status = skill.Status.ToString(),
        provenance = skill.Provenance.ToString(),
        permissions = skill.Package.Permissions,
        operatingSystems = skill.Package.Requirements.OperatingSystems,
        modelCapabilities = skill.Package.Requirements.ModelCapabilities,
        toolIds = skill.Package.Requirements.ToolIds,
        packageHash = skill.Package.PackageHash,
        skill.RecordVersion,
        skill.UpdatedAt,
    };

    private static object LearningSignalResponse(
        LearningSignal signal,
        LearningClassification classification) => new
        {
            id = signal.Id.Value,
            kind = signal.Kind.ToString(),
            summary = signal.RedactedSummary,
            action = classification.Action.ToString(),
            classification.ReasonCode,
            signal.OccurrenceCount,
            sourceRunId = signal.CausationId is { } causation &&
                causation.Value.StartsWith("run:", StringComparison.Ordinal)
                ? causation.Value[4..]
                : null,
            signal.SourceEvidenceHash,
            signal.SignalHash,
            classification.ClassificationHash,
            signal.CapturedAt,
        };

    private static IResult DomainProblem(HttpContext context, DomainFailure failure, string title) =>
        Problem(context, failure.Code switch
        {
            FailureCode.ConcurrencyConflict => 409,
            FailureCode.ApprovalRequired => 403,
            FailureCode.PolicyDenied => 403,
            FailureCode.UnsupportedCapability => 422,
            FailureCode.BudgetExceeded => 422,
            FailureCode.RecoverableExternalFailure => 503,
            _ => 400,
        }, title, failure.Message, failure.Code.ToString().ToLowerInvariant());

    private static IResult Problem(
        HttpContext context,
        int status,
        string title,
        string detail,
        string code) => Results.Problem(
        statusCode: status,
        title: title,
        detail: detail,
        type: $"urn:agentforge:problem:{code}",
        extensions: new Dictionary<string, object?> { ["correlationId"] = context.TraceIdentifier });

    private static IResult Replay(HttpContext context, object response)
    {
        context.Response.Headers["Idempotent-Replay"] = "true";
        return Results.Ok(response);
    }

    private static byte[] StableRequestIdentity(
        InstallationId installationId,
        string operation,
        string idempotencyKey) => SHA256.HashData(Encoding.UTF8.GetBytes(
        $"{installationId.Value:D}\n{operation}\n{idempotencyKey}"));

    private static string StoredIdempotencyKey(string operation, string idempotencyKey) =>
        $"ready-{operation}:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(idempotencyKey)))}";

    private static void RetainBoundedSessionResults(
        ConcurrentDictionary<string, ReadyAdminIdempotencyResult> results,
        int maximumBeforeInsert)
    {
        foreach (var key in results.Keys.Take(Math.Max(0, results.Count - maximumBeforeInsert)))
        {
            results.TryRemove(key, out _);
        }
    }

    private static string BuildSystemInstruction(
        AgentIdentity agent,
        string? runInstructions = null,
        IReadOnlyList<RunSkillBody>? skills = null)
    {
        var builder = new StringBuilder(1024);
        builder.Append("You are ").Append(agent.Name).Append(". ");
        if (!string.IsNullOrWhiteSpace(agent.Expertise))
        {
            builder.Append("Expertise: ").Append(agent.Expertise).Append(". ");
        }
        if (!string.IsNullOrWhiteSpace(agent.Mission))
        {
            builder.Append("Mission: ").Append(agent.Mission).Append(". ");
        }
        builder.Append("Respond in ").Append(agent.PreferredLanguage)
            .Append(" using this style: ").Append(agent.ResponseStyle).Append(". ");
        if (!string.IsNullOrWhiteSpace(runInstructions))
        {
            builder.Append("Operator guidance for this run: ")
                .Append(runInstructions).Append(". ");
        }
        foreach (var skill in skills ?? [])
        {
            builder.Append("Approved immutable skill ")
                .Append(skill.Id).Append('@').Append(skill.Version)
                .Append(" (package ").Append(skill.PackageHash).Append("):\n")
                .Append(skill.Body).Append("\nEnd approved skill. ");
        }
        builder.Append("Non-negotiable runtime boundary: do not claim to have used tools, browsing, network resources, files, memory, messages, devices, or external systems; none are available in this interactive run.");
        return builder.ToString();
    }

    private static object ResponseDepthOption(
        string id,
        string label,
        int requestedTokens,
        long agentMaximum) => new
        {
            id,
            label,
            maximumOutputTokens = (int)Math.Clamp(agentMaximum, 1L, requestedTokens),
        };

    private static int ResponseTokenLimit(string responseDepth, long agentMaximum) =>
        (int)Math.Clamp(agentMaximum, 1L, responseDepth switch
        {
            "concise" => 384,
            "detailed" => 2_048,
            _ => 1_024,
        });

    private static bool ValidStreamRequest(StreamAgentChatRequest request)
    {
        var depth = string.IsNullOrWhiteSpace(request.ResponseDepth)
            ? "balanced"
            : request.ResponseDepth.Trim().ToLowerInvariant();
        var skills = request.SkillIds ?? [];
        return PromptText(request.Prompt, 16_384) &&
            (string.IsNullOrWhiteSpace(request.Name) || Text(request.Name.Trim(), 120)) &&
            (string.IsNullOrWhiteSpace(request.RunInstructions) ||
                PromptText(request.RunInstructions.Trim(), 2_048)) &&
            depth is "concise" or "balanced" or "detailed" &&
            skills.Count <= 4 &&
            skills.All(SkillIdText) &&
            skills.Distinct(StringComparer.Ordinal).Count() == skills.Count;
    }

    private static bool SkillIdText(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 128 &&
        value.StartsWith("skill:", StringComparison.Ordinal) &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is ':' or '.' or '-' or '_');

    private static async ValueTask WriteSseAsync<T>(
        HttpContext context,
        string eventName,
        T payload,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(payload, HashJson);
        await context.Response.WriteAsync($"event: {eventName}\ndata: {json}\n\n", cancellationToken);
        await context.Response.Body.FlushAsync(cancellationToken);
    }

    private static async Task<bool> WasDurablyCanceledAsync(
        ITaskSnapshotStore snapshots,
        OrchestrationTaskId taskId)
    {
        var latest = await snapshots.FindLatestAsync(taskId, CancellationToken.None);
        return latest?.State is OrchestrationTaskState.Canceled;
    }

    private sealed class SseInteractionObserver(HttpContext context) : ILocalModelInteractionObserver
    {
        public ValueTask OnProgressAsync(
            LocalModelInteractionProgress progress,
            CancellationToken cancellationToken) => progress.Kind switch
            {
                LocalModelInteractionProgressKind.Started => WriteSseAsync(context, "model-started", new
                {
                    requestId = progress.RequestId.Value,
                    progress.ContextRedactionCount,
                }, cancellationToken),
                LocalModelInteractionProgressKind.TextDelta => WriteSseAsync(context, "output-delta", new
                {
                    requestId = progress.RequestId.Value,
                    text = progress.TextDelta,
                }, cancellationToken),
                LocalModelInteractionProgressKind.Usage => WriteSseAsync(context, "usage", new
                {
                    requestId = progress.RequestId.Value,
                    usage = progress.Usage,
                }, cancellationToken),
                _ => ValueTask.CompletedTask,
            };
    }

    private static string SnapshotHash<T>(T value) =>
        $"sha256:{Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, HashJson)))}";

    private static string SeedSkillDirectory() =>
        Path.Combine(AppContext.BaseDirectory, "skills", "seed", "csharp-review");

    private static bool Text(string? value, int maximum) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximum && !value.Any(char.IsControl);

    private static bool PromptText(string? value, int maximum) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximum &&
        !value.Any(character => char.IsControl(character) && character is not ('\r' or '\n' or '\t'));

    private static string? NormalizeLearningSummary(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 4_096)
        {
            return null;
        }

        var builder = new StringBuilder(value.Length);
        var pendingSpace = false;
        foreach (var character in value.Trim())
        {
            if (char.IsControl(character) && !char.IsWhiteSpace(character))
            {
                return null;
            }
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }
            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }
            builder.Append(character);
        }
        return builder.Length is > 0 and <= 4_096 ? builder.ToString() : null;
    }

    private static bool IsTrustedLoopback(HttpContext context)
    {
        var address = context.Connection.RemoteIpAddress;
        if (address is not null && !IPAddress.IsLoopback(address))
        {
            return false;
        }

        var origin = context.Request.Headers.Origin.ToString();
        return string.IsNullOrEmpty(origin) || Uri.TryCreate(origin, UriKind.Absolute, out var uri) &&
            string.Equals(uri.Scheme, context.Request.Scheme, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(uri.Authority, context.Request.Host.Value, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRemoteConnection(HttpContext context) =>
        context.Connection.RemoteIpAddress is { } address && !IPAddress.IsLoopback(address);

    private static bool IsTrustedWorkspaceRequest(HttpContext context) =>
        IsTrustedLoopback(context) || IsRemoteConnection(context) && context.Request.IsHttps;

    private static bool ValidRemoteAccessCode(HttpContext context, string configuredCode)
    {
        var supplied = context.Request.Headers["X-AgentForge-Remote-Access-Code"].ToString();
        if (string.IsNullOrEmpty(configuredCode) || string.IsNullOrEmpty(supplied) || supplied.Length > 256)
        {
            return false;
        }

        var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(configuredCode));
        var suppliedHash = SHA256.HashData(Encoding.UTF8.GetBytes(supplied));
        return CryptographicOperations.FixedTimeEquals(expectedHash, suppliedHash);
    }

    private sealed record ReadyAdminAcquisition(ReadyAdminSession? Session, IResult? Failure);
}
