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
    long? MaxOutputTokens);

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

        var providerUsers = (await agents.ListAsync(agent.InstallationId, cancellationToken))
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
            immutablePolicy = new
            {
                agent.ModelPolicy.DataLocality,
                agent.ModelPolicy.AllowFallback,
                agent.MemoryPolicy,
                agent.CapabilityPolicy,
                agent.Budget,
                agent.ChildLimits,
                agent.LearningPolicy,
            },
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
            var maxOutputTokens = request.MaxOutputTokens ?? agent.Budget.MaxOutputTokens;
            if (maxOutputTokens is < 256 or > 262_144)
            {
                return Problem(context, 400, "Invalid output ceiling",
                    "The Ready output-token ceiling must be between 256 and 262,144 tokens.",
                    "validationfailure");
            }

            var candidate = new AgentIdentityCandidate(
                request.Name,
                request.Expertise,
                request.Mission,
                request.PreferredLanguage,
                request.TimeZone,
                request.ResponseStyle,
                request.DefaultWorkspace,
                agent.ModelPolicy,
                agent.MemoryPolicy,
                agent.CapabilityPolicy,
                agent.Budget with { MaxOutputTokens = maxOutputTokens },
                agent.ChildLimits,
                agent.LearningPolicy);
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
                    "Change at least one identity or instruction field before previewing.", "validationfailure");
            }

            var allowedPaths = new HashSet<string>(StringComparer.Ordinal)
            {
                "agent.name", "agent.expertise", "agent.mission", "agent.preferredLanguage",
                "agent.timeZone", "agent.responseStyle", "agent.defaultWorkspace", "agent.budget",
            };
            if (preview.Value.Changes.Any(change => !allowedPaths.Contains(change.Path)))
            {
                return Problem(context, 403, "Profile edit boundary",
                    "The Ready profile editor can change only identity, instructions, and the bounded output-token ceiling.",
                    "policydenied");
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
                effective = new
                {
                    preview.Value.Effective.ProviderName,
                    preview.Value.Effective.Model,
                    capabilities = preview.Value.Effective.Capabilities,
                },
                immutableAuthorityPreserved = !preview.Value.Changes.Any(change => change.Path == "agent.budget"),
                warning = preview.Value.Changes.Any(change => change.Path == "agent.budget")
                    ? "This raises or lowers the agent's maximum generated-output budget. Other authority remains unchanged."
                    : "Only the displayed identity and instruction fields will change; effective authority is preserved.",
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
        budget = new
        {
            agent.Budget.MaxInputTokens,
            agent.Budget.MaxOutputTokens,
            agent.Budget.MaxWallClockSeconds,
        },
        agent.Version,
        agent.UpdatedAt,
    };

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
