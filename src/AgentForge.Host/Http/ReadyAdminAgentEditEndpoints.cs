using AgentForge.Abstractions.Agents;
using AgentForge.Abstractions.Installations;
using AgentForge.Abstractions.Models;
using AgentForge.Abstractions.Providers;
using AgentForge.Abstractions.Security;
using AgentForge.Abstractions.Setup;
using AgentForge.Abstractions.Time;
using AgentForge.Domain.Agents;
using AgentForge.Domain.Models;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Providers;
using AgentForge.Domain.Security;
using AgentForge.Domain.Setup;

namespace AgentForge.Host.Http;

internal sealed record ReadyModelEditWebRequest(
    long ExpectedInstallationVersion,
    long ExpectedProviderVersion,
    string Model);

internal sealed record ReadyAgentProfileEditWebRequest(
    long ExpectedInstallationVersion,
    long ExpectedAgentVersion,
    string Name,
    string? Expertise,
    string? Mission,
    string PreferredLanguage,
    string TimeZone,
    string ResponseStyle,
    string? DefaultWorkspace,
    long? MaxOutputTokens,
    Guid? PrimaryProviderId = null,
    string? DataLocality = null,
    bool? AllowFallback = null,
    string? MemoryScope = null,
    int? RetentionDays = null,
    string? NetworkPosture = null,
    IReadOnlyList<string>? ToolGrants = null,
    IReadOnlyList<string>? SkillGrants = null,
    int? MaxTurns = null,
    int? MaxToolInvocations = null,
    long? MaxInputTokens = null,
    int? MaxWallClockSeconds = null,
    long? ContextWindowOverrideTokens = null,
    bool? ContextCompressionEnabled = null,
    int? ContextCompressionThresholdPercent = null,
    int? ContextCompressionTargetPercent = null,
    int? ContextProtectedRecentTurns = null,
    bool? ClearContextWindowOverride = null,
    int? MaxChildDepth = null,
    int? MaxChildren = null,
    int? MaxChildConcurrency = null,
    long? MaxChildTokens = null,
    string? LearningMode = null,
    string? MutableSkillScope = null,
    long? ExpectedPrimaryProviderVersion = null);

internal sealed record ReadyEditApplyWebRequest(string PreviewHash);

internal static partial class ReadyAdminEndpoints
{
    private static async Task<IResult> GetAgentEditAsync(
        Guid agentId,
        HttpContext context,
        ReadyAdminSessionManager sessions,
        IInstallationStateReader stateReader,
        IInstallationRepository installations,
        IAgentIdentityRepository agents,
        IProviderProfileRepository providers,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var acquired = await AcquireAsync(
            context, sessions, stateReader, clock, requireCsrf: false, cancellationToken);
        if (acquired.Failure is not null)
        {
            return acquired.Failure;
        }

        var installation = await installations.ReadAsync(cancellationToken);
        var agent = await agents.FindByIdAsync(new AgentIdentityId(agentId), cancellationToken);
        if (agent is null || agent.InstallationId != acquired.Session!.InstallationId)
        {
            return Problem(context, 404, "Agent not found",
                "The requested agent does not belong to this installation.", "agent-not-found");
        }

        var provider = await providers.FindByIdAsync(agent.ModelPolicy.PrimaryProviderProfileId, cancellationToken);
        if (provider is null || provider.InstallationId != agent.InstallationId)
        {
            return Problem(context, 409, "Provider unavailable",
                "The agent's pinned provider profile is unavailable.", "provider-unavailable");
        }

        var allAgents = await agents.ListAsync(agent.InstallationId, cancellationToken);
        var allProviders = await providers.ListAsync(agent.InstallationId, cancellationToken);
        var providerUsers = allAgents
            .Where(item => item.ModelPolicy.PrimaryProviderProfileId == provider.Id)
            .Select(item => new { id = item.Id.Value, item.Name })
            .ToArray();
        return Results.Ok(new
        {
            installationVersion = installation.Version,
            agent = AgentEditResponse(agent),
            provider = new
            {
                id = provider.Id.Value,
                provider.Name,
                provider.ProviderType,
                endpoint = provider.Endpoint.AbsoluteUri,
                provider.Model,
                provider.Version,
                sharedBy = providerUsers,
            },
            providers = allProviders.Select(item => new
            {
                id = item.Id.Value,
                item.Name,
                item.ProviderType,
                endpoint = item.Endpoint.AbsoluteUri,
                item.Model,
                item.Version,
                item.Capabilities,
                sharedBy = allAgents.Count(identity =>
                    identity.ModelPolicy.PrimaryProviderProfileId == item.Id),
            }),
            policy = EffectivePolicyResponse(new EffectiveAgentDefinition(
                new AgentIdentityCandidate(
                    agent.Name,
                    agent.Expertise,
                    agent.Mission,
                    agent.PreferredLanguage,
                    agent.TimeZone,
                    agent.ResponseStyle,
                    agent.DefaultWorkspace,
                    agent.ModelPolicy,
                    agent.MemoryPolicy,
                    agent.CapabilityPolicy,
                    agent.Budget,
                    agent.ChildLimits,
                    agent.LearningPolicy),
                provider.Name,
                provider.Model,
                provider.Capabilities,
                [])),
            correlationId = context.TraceIdentifier,
        });
    }

    private static async Task<IResult> DiscoverAgentModelsAsync(
        Guid agentId,
        HttpContext context,
        ReadyAdminSessionManager sessions,
        IInstallationStateReader stateReader,
        IAgentIdentityRepository agents,
        IProviderProfileRepository providers,
        ISecretStore secretStore,
        IModelCatalogDiscoveryService discovery,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var acquired = await AcquireMutationAsync(context, sessions, stateReader, clock, cancellationToken);
        if (acquired.Failure is not null)
        {
            return acquired.Failure;
        }

        var session = acquired.Session!;
        var idempotencyKey = context.Request.Headers["Idempotency-Key"].ToString();
        var scopedKey = $"agent-model-discovery:{agentId:D}:{idempotencyKey}";
        var requestHash = SnapshotHash(new { agentId });
        await session.MutationGate.WaitAsync(cancellationToken);
        try
        {
            if (session.Results.TryGetValue(scopedKey, out var existing))
            {
                return string.Equals(existing.RequestHash, requestHash, StringComparison.Ordinal)
                    ? Replay(context, existing.Response)
                    : Problem(context, 409, "Idempotency conflict",
                        "The idempotency key is already bound to another model discovery request.",
                        "idempotency-conflict");
            }

            var resolved = await ResolveAgentProviderAsync(
                session.InstallationId, new AgentIdentityId(agentId), agents, providers, cancellationToken);
            if (!resolved.IsSuccess)
            {
                return DomainProblem(context, resolved.Failure!, "Model discovery failed");
            }

            var providerCredential = await MaterializeProviderCredentialAsync(
                resolved.Value.Provider, secretStore, cancellationToken);
            if (!providerCredential.IsSuccess)
            {
                return DomainProblem(context, providerCredential.Failure!, "Model discovery failed");
            }

            await using var credential = providerCredential.Value;
            var discovered = await discovery.DiscoverAsync(new ModelCatalogDiscoveryRequest(
                resolved.Value.Provider.Endpoint,
                resolved.Value.Provider.ProviderType,
                credential.Value), cancellationToken);
            if (!discovered.IsSuccess)
            {
                return DomainProblem(context, discovered.Failure!, "Model discovery failed");
            }

            var response = new
            {
                providerId = resolved.Value.Provider.Id.Value,
                providerVersion = resolved.Value.Provider.Version,
                selectedModel = resolved.Value.Provider.Model,
                models = discovered.Value.Models.Select(item => new
                {
                    id = item.Id,
                    item.OwnedBy,
                    item.MaximumContextTokens,
                }),
                catalogEndpoint = discovered.Value.CatalogEndpoint.AbsoluteUri,
                discovered.Value.ObservedAtUtc,
                correlationId = context.TraceIdentifier,
            };
            foreach (var model in discovered.Value.Models)
            {
                session.ModelCatalogObservations[ModelCatalogObservationKey(
                    resolved.Value.Provider.Id, model.Id)] = new ReadyModelCatalogObservation(
                        resolved.Value.Provider.Id,
                        resolved.Value.Provider.Version,
                        model.Id,
                        model.MaximumContextTokens,
                        discovered.Value.ObservedAtUtc);
            }
            StoreIdempotentResult(session, scopedKey, requestHash, response);
            return Results.Ok(response);
        }
        finally
        {
            session.MutationGate.Release();
        }
    }

    private static async Task<IResult> PreviewAgentModelAsync(
        Guid agentId,
        ReadyModelEditWebRequest request,
        HttpContext context,
        ReadyAdminSessionManager sessions,
        IInstallationStateReader stateReader,
        IAgentIdentityRepository agents,
        IProviderProfileRepository providers,
        ILocalAdministratorRepository administrators,
        ISecretStore secretStore,
        IModelCatalogDiscoveryService discovery,
        ISetupProfileEditor editor,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var acquired = await AcquireMutationAsync(context, sessions, stateReader, clock, cancellationToken);
        if (acquired.Failure is not null)
        {
            return acquired.Failure;
        }
        var model = (request.Model ?? string.Empty).Trim();
        if (!Text(model, 256))
        {
            return Problem(context, 400, "Invalid model",
                "Select a bounded model identifier before previewing the change.", "validationfailure");
        }

        var session = acquired.Session!;
        var idempotencyKey = context.Request.Headers["Idempotency-Key"].ToString();
        var scopedKey = $"agent-model-preview:{agentId:D}:{idempotencyKey}";
        var requestHash = SnapshotHash(new { agentId, request.ExpectedInstallationVersion, request.ExpectedProviderVersion, model });
        await session.MutationGate.WaitAsync(cancellationToken);
        try
        {
            if (session.Results.TryGetValue(scopedKey, out var existing))
            {
                return string.Equals(existing.RequestHash, requestHash, StringComparison.Ordinal)
                    ? Replay(context, existing.Response)
                    : Problem(context, 409, "Idempotency conflict",
                        "The idempotency key is already bound to another model edit preview.",
                        "idempotency-conflict");
            }

            var resolved = await ResolveAgentProviderAsync(
                session.InstallationId, new AgentIdentityId(agentId), agents, providers, cancellationToken);
            if (!resolved.IsSuccess)
            {
                return DomainProblem(context, resolved.Failure!, "Model edit preview failed");
            }

            var provider = resolved.Value.Provider;
            var providerCredential = await MaterializeProviderCredentialAsync(provider, secretStore, cancellationToken);
            if (!providerCredential.IsSuccess)
            {
                return DomainProblem(context, providerCredential.Failure!, "Model verification failed");
            }

            ModelConnectionProbeResult probe;
            await using (var credential = providerCredential.Value)
            {
                var probed = await discovery.ProbeAsync(new ModelConnectionProbeRequest(
                    provider.Endpoint, provider.ProviderType, model, credential.Value), cancellationToken);
                if (!probed.IsSuccess)
                {
                    return DomainProblem(context, probed.Failure!, "Model verification failed");
                }
                probe = probed.Value;
            }

            var administratorCredential = await MaterializeAdministratorCredentialAsync(
                session.InstallationId, administrators, secretStore, cancellationToken);
            if (!administratorCredential.IsSuccess)
            {
                return DomainProblem(context, administratorCredential.Failure!, "Model edit preview failed");
            }

            var stable = StableRequestIdentity(session.InstallationId, "agent-model-preview", idempotencyKey);
            var correlation = new CorrelationId($"admin-model-edit:{Convert.ToHexStringLower(stable.AsSpan(0, 16))}");
            var candidate = new ProviderProfileCandidate(
                provider.Name, provider.ProviderType, provider.Endpoint, model, provider.SecretReference);
            DomainResult<ProviderEditPreview> preview;
            await using (var credential = administratorCredential.Value)
            {
                preview = await editor.PreviewProviderAsync(new PreviewProviderEditRequest(
                    provider.Id,
                    request.ExpectedInstallationVersion,
                    request.ExpectedProviderVersion,
                    candidate,
                    session.ActorId,
                    correlation,
                    credential.Value), cancellationToken);
            }
            if (!preview.IsSuccess)
            {
                return DomainProblem(context, preview.Failure!, "Model edit preview failed");
            }
            if (preview.Value.Changes.Count == 0)
            {
                return Problem(context, 400, "No model change",
                    "Select a model different from the currently configured model.", "validationfailure");
            }
            if (preview.Value.Changes.Any(change => change.Path is not "provider.model"))
            {
                return Problem(context, 403, "Model-only edit required",
                    "The Ready workspace may change only the selected model on the existing provider connection.",
                    "policydenied");
            }

            RetainBoundedPreviews(session.ProviderPreviews, 7);
            session.ProviderPreviews[preview.Value.RequestHash] = new ReadyProviderEditPreview(
                resolved.Value.Agent.Id,
                provider.Id,
                request.ExpectedInstallationVersion,
                request.ExpectedProviderVersion,
                candidate,
                correlation,
                preview.Value.RequestHash);
            var affected = (await agents.ListAsync(session.InstallationId, cancellationToken))
                .Where(item => item.ModelPolicy.PrimaryProviderProfileId == provider.Id)
                .Select(item => item.Name)
                .ToArray();
            var response = new
            {
                previewHash = preview.Value.RequestHash,
                changes = preview.Value.Changes.Select(ChangeResponse),
                verification = new
                {
                    probe.Model,
                    endpoint = probe.ProbeEndpoint.AbsoluteUri,
                    durationMilliseconds = Math.Max(0, probe.Duration.TotalMilliseconds),
                    probe.Evidence,
                },
                affectedAgents = affected,
                warning = affected.Length > 1
                    ? "This shared provider model will change for every listed agent."
                    : "The selected model will change for this agent's pinned provider.",
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

    private static async Task<IResult> ApplyAgentModelAsync(
        Guid agentId,
        ReadyEditApplyWebRequest request,
        HttpContext context,
        ReadyAdminSessionManager sessions,
        IInstallationStateReader stateReader,
        IProviderProfileRepository providers,
        ILocalAdministratorRepository administrators,
        ISecretStore secretStore,
        IModelCatalogDiscoveryService discovery,
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
        var idempotencyKey = context.Request.Headers["Idempotency-Key"].ToString();
        var scopedKey = $"agent-model-apply:{agentId:D}:{idempotencyKey}";
        var requestHash = SnapshotHash(new { agentId, request.PreviewHash });
        await session.MutationGate.WaitAsync(cancellationToken);
        try
        {
            if (session.Results.TryGetValue(scopedKey, out var existing))
            {
                return string.Equals(existing.RequestHash, requestHash, StringComparison.Ordinal)
                    ? Replay(context, existing.Response)
                    : Problem(context, 409, "Idempotency conflict",
                        "The idempotency key is already bound to another model edit.", "idempotency-conflict");
            }
            if (!Text(request.PreviewHash, 128) ||
                !session.ProviderPreviews.TryGetValue(request.PreviewHash, out var approved) ||
                approved.AgentId != new AgentIdentityId(agentId))
            {
                return Problem(context, 403, "Approved preview required",
                    "Preview the exact model change in this operator session before applying it.", "policydenied");
            }

            var provider = await providers.FindByIdAsync(approved.ProviderId, cancellationToken);
            if (provider is null || provider.InstallationId != session.InstallationId)
            {
                return Problem(context, 409, "Provider changed",
                    "The pinned provider is no longer available; refresh the editor.", "concurrency-conflict");
            }

            var providerCredential = await MaterializeProviderCredentialAsync(provider, secretStore, cancellationToken);
            if (!providerCredential.IsSuccess)
            {
                return DomainProblem(context, providerCredential.Failure!, "Model verification failed");
            }
            await using (var credential = providerCredential.Value)
            {
                var probe = await discovery.ProbeAsync(new ModelConnectionProbeRequest(
                    approved.Candidate.Endpoint,
                    approved.Candidate.ProviderType,
                    approved.Candidate.Model,
                    credential.Value), cancellationToken);
                if (!probe.IsSuccess)
                {
                    return DomainProblem(context, probe.Failure!, "Model verification failed");
                }
            }

            var administratorCredential = await MaterializeAdministratorCredentialAsync(
                session.InstallationId, administrators, secretStore, cancellationToken);
            if (!administratorCredential.IsSuccess)
            {
                return DomainProblem(context, administratorCredential.Failure!, "Model edit failed");
            }

            DomainResult<ProviderEditResult> applied;
            await using (var credential = administratorCredential.Value)
            {
                applied = await editor.ApplyProviderAsync(new ApplyProviderEditRequest(
                    approved.ProviderId,
                    approved.ExpectedInstallationVersion,
                    approved.ExpectedProviderVersion,
                    approved.Candidate,
                    approved.RequestHash,
                    session.ActorId,
                    approved.CorrelationId,
                    credential.Value), cancellationToken);
            }
            if (!applied.IsSuccess)
            {
                return DomainProblem(context, applied.Failure!, "Model edit failed");
            }

            var response = new
            {
                installationVersion = applied.Value.Installation.Version,
                provider = new
                {
                    id = applied.Value.Provider.Id.Value,
                    applied.Value.Provider.Name,
                    applied.Value.Provider.Model,
                    applied.Value.Provider.Version,
                },
                changes = applied.Value.Changes.Select(ChangeResponse),
                previewHash = applied.Value.RequestHash,
                correlationId = approved.CorrelationId.Value,
            };
            session.ProviderPreviews.TryRemove(request.PreviewHash, out _);
            StoreIdempotentResult(session, scopedKey, requestHash, response);
            return Results.Ok(response);
        }
        finally
        {
            session.MutationGate.Release();
        }
    }

    private static async Task<IResult> PreviewAgentProfileAsync(
        Guid agentId,
        ReadyAgentProfileEditWebRequest request,
        HttpContext context,
        ReadyAdminSessionManager sessions,
        IInstallationStateReader stateReader,
        IAgentIdentityRepository agents,
        IProviderProfileRepository providers,
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
        var idempotencyKey = context.Request.Headers["Idempotency-Key"].ToString();
        var scopedKey = $"agent-profile-preview:{agentId:D}:{idempotencyKey}";
        var requestHash = SnapshotHash(new { agentId, request });
        await session.MutationGate.WaitAsync(cancellationToken);
        try
        {
            if (session.Results.TryGetValue(scopedKey, out var existing))
            {
                return string.Equals(existing.RequestHash, requestHash, StringComparison.Ordinal)
                    ? Replay(context, existing.Response)
                    : Problem(context, 409, "Idempotency conflict",
                        "The idempotency key is already bound to another agent profile preview.",
                        "idempotency-conflict");
            }

            var agent = await agents.FindByIdAsync(new AgentIdentityId(agentId), cancellationToken);
            if (agent is null || agent.InstallationId != session.InstallationId)
            {
                return Problem(context, 404, "Agent not found",
                    "The requested agent does not belong to this installation.", "agent-not-found");
            }
            var targetProviderId = request.PrimaryProviderId is { } requestedProviderId
                ? new ProviderProfileId(requestedProviderId)
                : agent.ModelPolicy.PrimaryProviderProfileId;
            var targetProvider = await providers.FindByIdAsync(
                targetProviderId, cancellationToken);
            if (targetProvider is null || targetProvider.InstallationId != session.InstallationId)
            {
                return Problem(context, 400, "Provider unavailable",
                    "The selected primary provider does not belong to this installation.", "validationfailure");
            }
            if (request.ExpectedPrimaryProviderVersion is { } expectedProviderVersion &&
                targetProvider.Version != expectedProviderVersion)
            {
                return Problem(context, 409, "Provider changed",
                    "The selected provider version changed; refresh the policy editor.", "concurrency-conflict");
            }
            var observation = FindContextObservation(session, targetProvider);
            var discoveredContextWindow = observation?.MaximumContextTokens;
            if (discoveredContextWindow is null &&
                targetProvider.Id == agent.ModelPolicy.PrimaryProviderProfileId &&
                string.Equals(targetProvider.Model, agent.Budget.DiscoveredContextModel, StringComparison.Ordinal))
            {
                discoveredContextWindow = agent.Budget.DiscoveredContextWindowTokens;
            }
            var candidateResult = AgentCandidate(
                agent,
                request,
                discoveredContextWindow,
                discoveredContextWindow.HasValue ? targetProvider.Model : null);
            if (!candidateResult.IsSuccess)
            {
                return DomainProblem(context, candidateResult.Failure!, "Agent policy preview failed");
            }
            var candidate = candidateResult.Value;
            var administratorCredential = await MaterializeAdministratorCredentialAsync(
                session.InstallationId, administrators, secretStore, cancellationToken);
            if (!administratorCredential.IsSuccess)
            {
                return DomainProblem(context, administratorCredential.Failure!, "Agent profile preview failed");
            }

            var stable = StableRequestIdentity(session.InstallationId, "agent-profile-preview", idempotencyKey);
            var correlation = new CorrelationId($"admin-agent-edit:{Convert.ToHexStringLower(stable.AsSpan(0, 16))}");
            DomainResult<AgentEditPreview> preview;
            await using (var credential = administratorCredential.Value)
            {
                preview = await editor.PreviewAgentAsync(new PreviewAgentEditRequest(
                    agent.Id,
                    request.ExpectedInstallationVersion,
                    request.ExpectedAgentVersion,
                    candidate,
                    session.ActorId,
                    correlation,
                    credential.Value), cancellationToken);
            }
            if (!preview.IsSuccess)
            {
                return DomainProblem(context, preview.Failure!, "Agent profile preview failed");
            }
            if (preview.Value.Changes.Count == 0)
            {
                return Problem(context, 400, "No profile change",
                    "Change at least one identity or policy field before previewing.", "validationfailure");
            }

            RetainBoundedPreviews(session.AgentPreviews, 7);
            session.AgentPreviews[preview.Value.RequestHash] = new ReadyAgentEditPreview(
                agent.Id,
                request.ExpectedInstallationVersion,
                request.ExpectedAgentVersion,
                candidate,
                correlation,
                preview.Value.RequestHash);
            var response = new
            {
                previewHash = preview.Value.RequestHash,
                changes = preview.Value.Changes.Select(ChangeResponse),
                effective = EffectivePolicyResponse(preview.Value.Effective),
                impact = new
                {
                    currentAgentVersion = agent.Version,
                    nextAgentVersion = checked(agent.Version + 1),
                    providerVersion = targetProvider.Version,
                    invalidatesConversationContinuation = true,
                    requiresNewConversation = true,
                    authorityChanged = preview.Value.Changes.Any(change => change.Path is
                        "agent.modelPolicy" or "agent.memoryPolicy" or "agent.capabilityPolicy" or
                        "agent.budget" or "agent.childLimits" or "agent.learningPolicy"),
                },
                warning = "Applying this exact preview creates a new agent policy version. Existing run snapshots remain immutable; continue with a new conversation.",
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

    private static async Task<IResult> ApplyAgentProfileAsync(
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
        var idempotencyKey = context.Request.Headers["Idempotency-Key"].ToString();
        var scopedKey = $"agent-profile-apply:{agentId:D}:{idempotencyKey}";
        var requestHash = SnapshotHash(new { agentId, request.PreviewHash });
        await session.MutationGate.WaitAsync(cancellationToken);
        try
        {
            if (session.Results.TryGetValue(scopedKey, out var existing))
            {
                return string.Equals(existing.RequestHash, requestHash, StringComparison.Ordinal)
                    ? Replay(context, existing.Response)
                    : Problem(context, 409, "Idempotency conflict",
                        "The idempotency key is already bound to another agent profile edit.",
                        "idempotency-conflict");
            }
            if (!Text(request.PreviewHash, 128) ||
                !session.AgentPreviews.TryGetValue(request.PreviewHash, out var approved) ||
                approved.AgentId != new AgentIdentityId(agentId))
            {
                return Problem(context, 403, "Approved preview required",
                    "Preview the exact agent identity change in this operator session before applying it.",
                    "policydenied");
            }

            var administratorCredential = await MaterializeAdministratorCredentialAsync(
                session.InstallationId, administrators, secretStore, cancellationToken);
            if (!administratorCredential.IsSuccess)
            {
                return DomainProblem(context, administratorCredential.Failure!, "Agent profile edit failed");
            }

            DomainResult<AgentEditResult> applied;
            await using (var credential = administratorCredential.Value)
            {
                applied = await editor.ApplyAgentAsync(new ApplyAgentEditRequest(
                    approved.AgentId,
                    approved.ExpectedInstallationVersion,
                    approved.ExpectedAgentVersion,
                    approved.Candidate,
                    approved.RequestHash,
                    session.ActorId,
                    approved.CorrelationId,
                    credential.Value), cancellationToken);
            }
            if (!applied.IsSuccess)
            {
                return DomainProblem(context, applied.Failure!, "Agent profile edit failed");
            }

            var response = new
            {
                installationVersion = applied.Value.Installation.Version,
                agent = AgentEditResponse(applied.Value.Agent),
                changes = applied.Value.Changes.Select(ChangeResponse),
                previewHash = applied.Value.RequestHash,
                correlationId = approved.CorrelationId.Value,
            };
            session.AgentPreviews.TryRemove(request.PreviewHash, out _);
            StoreIdempotentResult(session, scopedKey, requestHash, response);
            return Results.Ok(response);
        }
        finally
        {
            session.MutationGate.Release();
        }
    }

    private static DomainResult<AgentIdentityCandidate> AgentCandidate(
        AgentIdentity current,
        ReadyAgentProfileEditWebRequest request,
        long? discoveredContextWindowTokens,
        string? discoveredContextModel)
    {
        var dataLocality = current.ModelPolicy.DataLocality;
        var memoryScope = current.MemoryPolicy.Scope;
        var networkPosture = current.CapabilityPolicy.NetworkPosture;
        var learningMode = current.LearningPolicy.Mode;
        var mutableSkillScope = current.LearningPolicy.MutableSkillScope;
        if (request.DataLocality is not null && !TryEnum(request.DataLocality, out dataLocality) ||
            request.MemoryScope is not null && !TryEnum(request.MemoryScope, out memoryScope) ||
            request.NetworkPosture is not null && !TryEnum(request.NetworkPosture, out networkPosture) ||
            request.LearningMode is not null && !TryEnum(request.LearningMode, out learningMode) ||
            request.MutableSkillScope is not null && !TryEnum(request.MutableSkillScope, out mutableSkillScope))
        {
            return DomainResult.Fail<AgentIdentityCandidate>(new DomainFailure(
                FailureCode.ValidationFailure,
                "Agent policy contains an invalid enum selection."));
        }

        return DomainResult.Success(new AgentIdentityCandidate(
            request.Name,
            request.Expertise,
            request.Mission,
            request.PreferredLanguage,
            request.TimeZone,
            request.ResponseStyle,
            request.DefaultWorkspace,
            new AgentModelPolicy(
                request.PrimaryProviderId is { } providerId
                    ? new ProviderProfileId(providerId)
                    : current.ModelPolicy.PrimaryProviderProfileId,
                dataLocality,
                request.AllowFallback ?? current.ModelPolicy.AllowFallback),
            new AgentMemoryPolicy(
                memoryScope,
                request.RetentionDays ?? current.MemoryPolicy.RetentionDays),
            new AgentCapabilityPolicy(
                networkPosture,
                request.ToolGrants ?? current.CapabilityPolicy.ToolGrants,
                request.SkillGrants ?? current.CapabilityPolicy.SkillGrants),
            new AgentBudget(
                request.MaxTurns ?? current.Budget.MaxTurns,
                request.MaxToolInvocations ?? current.Budget.MaxToolInvocations,
                request.MaxInputTokens ?? current.Budget.MaxInputTokens,
                request.MaxOutputTokens ?? current.Budget.MaxOutputTokens,
                request.MaxWallClockSeconds ?? current.Budget.MaxWallClockSeconds)
            {
                DiscoveredContextWindowTokens = discoveredContextWindowTokens,
                DiscoveredContextModel = discoveredContextModel,
                ContextWindowOverrideTokens = request.ContextWindowOverrideTokens ??
                    (request.ClearContextWindowOverride is true
                        ? null
                        : current.Budget.ContextWindowOverrideTokens),
                ContextCompressionEnabled = request.ContextCompressionEnabled ?? current.Budget.ContextCompressionEnabled,
                ContextCompressionThresholdPercent = request.ContextCompressionThresholdPercent ?? current.Budget.ContextCompressionThresholdPercent,
                ContextCompressionTargetPercent = request.ContextCompressionTargetPercent ?? current.Budget.ContextCompressionTargetPercent,
                ContextProtectedRecentTurns = request.ContextProtectedRecentTurns ?? current.Budget.ContextProtectedRecentTurns,
            },
            new ChildAgentLimits(
                request.MaxChildDepth ?? current.ChildLimits.MaxDepth,
                request.MaxChildren ?? current.ChildLimits.MaxChildren,
                request.MaxChildConcurrency ?? current.ChildLimits.MaxConcurrency,
                request.MaxChildTokens ?? current.ChildLimits.MaxTotalTokens),
            new AgentLearningPolicy(learningMode, mutableSkillScope)));
    }

    private static async Task<DomainResult<(AgentIdentity Agent, ProviderProfile Provider)>> ResolveAgentProviderAsync(
        InstallationId installationId,
        AgentIdentityId agentId,
        IAgentIdentityRepository agents,
        IProviderProfileRepository providers,
        CancellationToken cancellationToken)
    {
        var agent = await agents.FindByIdAsync(agentId, cancellationToken);
        if (agent is null || agent.InstallationId != installationId)
        {
            return DomainResult.Fail<(AgentIdentity, ProviderProfile)>(new DomainFailure(
                FailureCode.ValidationFailure, "The requested agent does not belong to this installation."));
        }

        var provider = await providers.FindByIdAsync(agent.ModelPolicy.PrimaryProviderProfileId, cancellationToken);
        return provider is null || provider.InstallationId != installationId
            ? DomainResult.Fail<(AgentIdentity, ProviderProfile)>(new DomainFailure(
                FailureCode.ConcurrencyConflict, "The agent's pinned provider profile is unavailable."))
            : DomainResult.Success((agent, provider));
    }

    private static async Task<DomainResult<SecretLease>> MaterializeAdministratorCredentialAsync(
        InstallationId installationId,
        ILocalAdministratorRepository administrators,
        ISecretStore secretStore,
        CancellationToken cancellationToken)
    {
        var administrator = await administrators.FindAsync(installationId, cancellationToken);
        if (administrator is null)
        {
            return DomainResult.Fail<SecretLease>(new DomainFailure(
                FailureCode.InvalidStateTransition, "The local administrator record is unavailable."));
        }

        return await secretStore.MaterializeAsync(administrator.ClientCredentialReference, cancellationToken);
    }

    private static Task<DomainResult<SecretLease>> MaterializeProviderCredentialAsync(
        ProviderProfile provider,
        ISecretStore secretStore,
        CancellationToken cancellationToken) => provider.SecretReference.IsNoCredential
            ? Task.FromResult(DomainResult.Success(new SecretLease([])))
            : secretStore.MaterializeAsync(provider.SecretReference, cancellationToken);

    private static object AgentEditResponse(AgentIdentity agent) => new
    {
        id = agent.Id.Value,
        agent.Name,
        agent.Expertise,
        agent.Mission,
        agent.PreferredLanguage,
        agent.TimeZone,
        agent.ResponseStyle,
        agent.DefaultWorkspace,
        agent.ModelPolicy,
        agent.MemoryPolicy,
        agent.CapabilityPolicy,
        budget = new
        {
            agent.Budget.MaxTurns,
            agent.Budget.MaxToolInvocations,
            agent.Budget.MaxInputTokens,
            agent.Budget.MaxOutputTokens,
            agent.Budget.MaxWallClockSeconds,
            agent.Budget.DiscoveredContextWindowTokens,
            agent.Budget.DiscoveredContextModel,
            agent.Budget.ContextWindowOverrideTokens,
            agent.Budget.EffectiveContextWindowTokens,
            agent.Budget.ContextWindowSource,
            agent.Budget.ContextCompressionEnabled,
            agent.Budget.ContextCompressionThresholdPercent,
            agent.Budget.ContextCompressionTargetPercent,
            agent.Budget.ContextProtectedRecentTurns,
        },
        agent.ChildLimits,
        agent.LearningPolicy,
        agent.Version,
        agent.UpdatedAt,
    };

    private static string ModelCatalogObservationKey(ProviderProfileId providerId, string model) =>
        $"{providerId.Value:D}:{model}";

    private static ReadyModelCatalogObservation? FindContextObservation(
        ReadyAdminSession session,
        ProviderProfile provider) =>
        session.ModelCatalogObservations.TryGetValue(
            ModelCatalogObservationKey(provider.Id, provider.Model), out var observation) &&
        observation.ProviderVersion == provider.Version
            ? observation
            : null;

    private static object ChangeResponse(SetupProfileChange change) => new
    {
        change.Path,
        change.Before,
        change.After,
    };

    private static void StoreIdempotentResult(
        ReadyAdminSession session,
        string key,
        string requestHash,
        object response)
    {
        RetainBoundedSessionResults(session.Results, 31);
        session.Results[key] = new ReadyAdminIdempotencyResult(requestHash, response);
    }

    private static void RetainBoundedPreviews<T>(
        System.Collections.Concurrent.ConcurrentDictionary<string, T> previews,
        int maximumBeforeInsert)
    {
        foreach (var key in previews.Keys.Take(Math.Max(0, previews.Count - maximumBeforeInsert)))
        {
            previews.TryRemove(key, out _);
        }
    }
}
