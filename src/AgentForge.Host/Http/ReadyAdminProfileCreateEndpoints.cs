using System.Security.Cryptography;
using System.Text;
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

internal sealed record ReadyProviderCreatePreviewWebRequest(
    long ExpectedInstallationVersion,
    string Name,
    string ProviderType,
    string Endpoint,
    string Model,
    string? Credential);

internal sealed record ReadyProviderCreateApplyWebRequest(string PreviewHash, string? Credential);

internal sealed record ReadyAgentCreateWebRequest(
    long ExpectedInstallationVersion,
    long ExpectedProviderVersion,
    string Name,
    string? Expertise,
    string? Mission,
    string PreferredLanguage,
    string TimeZone,
    string ResponseStyle,
    string? DefaultWorkspace,
    Guid PrimaryProviderId,
    string DataLocality,
    bool AllowFallback,
    string MemoryScope,
    int RetentionDays,
    string NetworkPosture,
    IReadOnlyList<string>? ToolGrants,
    IReadOnlyList<string>? SkillGrants,
    int MaxTurns,
    int MaxToolInvocations,
    long MaxInputTokens,
    long MaxOutputTokens,
    int MaxWallClockSeconds,
    long? ContextWindowOverrideTokens,
    bool? ContextCompressionEnabled,
    int? ContextCompressionThresholdPercent,
    int? ContextCompressionTargetPercent,
    int? ContextProtectedRecentTurns,
    int MaxChildDepth,
    int MaxChildren,
    int MaxChildConcurrency,
    long MaxChildTokens,
    string LearningMode,
    string MutableSkillScope);

internal static partial class ReadyAdminEndpoints
{
    private static async Task<IResult> ListProvidersAsync(
        HttpContext context,
        ReadyAdminSessionManager sessions,
        IInstallationStateReader stateReader,
        IInstallationRepository installations,
        IProviderProfileRepository providers,
        IAgentIdentityRepository agents,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var acquired = await AcquireAsync(
            context, sessions, stateReader, clock, requireCsrf: false, cancellationToken);
        if (acquired.Failure is not null) return acquired.Failure;

        var installation = await installations.ReadAsync(cancellationToken);
        var profiles = await providers.ListAsync(acquired.Session!.InstallationId, cancellationToken);
        var identities = await agents.ListAsync(acquired.Session.InstallationId, cancellationToken);
        return Results.Ok(new
        {
            installationVersion = installation.Version,
            providers = profiles.Select(profile => new
            {
                id = profile.Id.Value,
                profile.Name,
                profile.ProviderType,
                endpoint = profile.Endpoint.AbsoluteUri,
                profile.Model,
                authentication = profile.SecretReference.IsNoCredential ? "No API key" : "OS-backed secret",
                profile.Capabilities,
                profile.Version,
                sharedBy = identities
                    .Where(agent => agent.ModelPolicy.PrimaryProviderProfileId == profile.Id)
                    .Select(agent => new { id = agent.Id.Value, agent.Name })
                    .ToArray(),
            }),
            correlationId = context.TraceIdentifier,
        });
    }

    private static async Task<IResult> PreviewProviderCreateAsync(
        ReadyProviderCreatePreviewWebRequest request,
        HttpContext context,
        ReadyAdminSessionManager sessions,
        IInstallationStateReader stateReader,
        ILocalAdministratorRepository administrators,
        ISecretStore secretStore,
        IModelCatalogDiscoveryService discovery,
        ISetupProfileEditor editor,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var acquired = await AcquireMutationAsync(context, sessions, stateReader, clock, cancellationToken);
        if (acquired.Failure is not null) return acquired.Failure;
        if (!Uri.TryCreate(request.Endpoint?.Trim(), UriKind.Absolute, out var endpoint) ||
            !Text(request.Name, 128) || !Text(request.ProviderType, 64) || !Text(request.Model, 256) ||
            request.Credential is { Length: > 8192 })
        {
            return Problem(context, 400, "Invalid provider profile",
                "Enter bounded provider fields and an absolute endpoint before previewing.", "validationfailure");
        }

        var session = acquired.Session!;
        var idempotencyKey = context.Request.Headers["Idempotency-Key"].ToString();
        var credential = request.Credential?.AsMemory() ?? ReadOnlyMemory<char>.Empty;
        var credentialFingerprint = CredentialFingerprint(credential.Span);
        var webHash = SnapshotHash(new
        {
            request.ExpectedInstallationVersion,
            request.Name,
            request.ProviderType,
            Endpoint = endpoint.AbsoluteUri,
            request.Model,
            CredentialFingerprint = credentialFingerprint,
        });
        var scopedKey = $"provider-create-preview:{idempotencyKey}";
        await session.MutationGate.WaitAsync(cancellationToken);
        try
        {
            if (session.Results.TryGetValue(scopedKey, out var existing))
            {
                return string.Equals(existing.RequestHash, webHash, StringComparison.Ordinal)
                    ? Replay(context, existing.Response)
                    : Problem(context, 409, "Idempotency conflict",
                        "The idempotency key is already bound to another provider preview.", "idempotency-conflict");
            }

            var providerType = request.ProviderType.Trim().ToLowerInvariant();
            var model = request.Model.Trim();
            var probed = await discovery.ProbeAsync(new ModelConnectionProbeRequest(
                endpoint, providerType, model, credential), cancellationToken);
            if (!probed.IsSuccess)
            {
                return DomainProblem(context, probed.Failure!, "Provider connection verification failed");
            }

            var administratorCredential = await MaterializeAdministratorCredentialAsync(
                session.InstallationId, administrators, secretStore, cancellationToken);
            if (!administratorCredential.IsSuccess)
            {
                return DomainProblem(context, administratorCredential.Failure!, "Provider creation preview failed");
            }

            var stable = StableRequestIdentity(session.InstallationId, "provider-create-preview", idempotencyKey);
            var correlation = new CorrelationId($"admin-provider-create:{Convert.ToHexStringLower(stable.AsSpan(0, 16))}");
            var candidate = new ProviderProfileCandidate(
                request.Name,
                providerType,
                endpoint,
                model,
                SecretReference.NoCredential);
            DomainResult<ProviderCreatePreview> preview;
            await using (var lease = administratorCredential.Value)
            {
                preview = await editor.PreviewProviderCreateAsync(new PreviewProviderCreateRequest(
                    request.ExpectedInstallationVersion,
                    candidate,
                    credential,
                    session.ActorId,
                    correlation,
                    lease.Value), cancellationToken);
            }
            if (!preview.IsSuccess)
            {
                return DomainProblem(context, preview.Failure!, "Provider creation preview failed");
            }

            RetainBoundedPreviews(session.ProviderCreatePreviews, 7);
            session.ProviderCreatePreviews[preview.Value.RequestHash] = new ReadyProviderCreatePreview(
                request.ExpectedInstallationVersion,
                candidate,
                preview.Value.UsesCredential,
                credentialFingerprint,
                correlation,
                preview.Value.RequestHash);
            var response = new
            {
                previewHash = preview.Value.RequestHash,
                changes = preview.Value.Changes.Select(ChangeResponse),
                capabilities = preview.Value.Capabilities,
                verification = new
                {
                    probed.Value.Model,
                    endpoint = probed.Value.ProbeEndpoint.AbsoluteUri,
                    durationMilliseconds = Math.Max(0, probed.Value.Duration.TotalMilliseconds),
                    probed.Value.Evidence,
                },
                authentication = preview.Value.UsesCredential ? "OS-backed secret" : "No API key",
                warning = "Creating this provider grants no agent authority until an exact agent policy selects it.",
                correlationId = correlation.Value,
            };
            StoreIdempotentResult(session, scopedKey, webHash, response);
            return Results.Ok(response);
        }
        finally
        {
            session.MutationGate.Release();
        }
    }

    private static async Task<IResult> ApplyProviderCreateAsync(
        ReadyProviderCreateApplyWebRequest request,
        HttpContext context,
        ReadyAdminSessionManager sessions,
        IInstallationStateReader stateReader,
        ILocalAdministratorRepository administrators,
        ISecretStore secretStore,
        IModelCatalogDiscoveryService discovery,
        ISetupProfileEditor editor,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var acquired = await AcquireMutationAsync(context, sessions, stateReader, clock, cancellationToken);
        if (acquired.Failure is not null) return acquired.Failure;

        var session = acquired.Session!;
        var credential = request.Credential?.AsMemory() ?? ReadOnlyMemory<char>.Empty;
        var credentialFingerprint = CredentialFingerprint(credential.Span);
        var idempotencyKey = context.Request.Headers["Idempotency-Key"].ToString();
        var webHash = SnapshotHash(new
        {
            request.PreviewHash,
            CredentialFingerprint = credentialFingerprint,
        });
        var scopedKey = $"provider-create-apply:{idempotencyKey}";
        await session.MutationGate.WaitAsync(cancellationToken);
        try
        {
            if (session.Results.TryGetValue(scopedKey, out var existing))
            {
                return string.Equals(existing.RequestHash, webHash, StringComparison.Ordinal)
                    ? Replay(context, existing.Response)
                    : Problem(context, 409, "Idempotency conflict",
                        "The idempotency key is already bound to another provider creation.", "idempotency-conflict");
            }
            if (!Text(request.PreviewHash, 128) ||
                !session.ProviderCreatePreviews.TryGetValue(request.PreviewHash, out var approved) ||
                approved.UsesCredential != !credential.IsEmpty ||
                !string.Equals(approved.CredentialFingerprint, credentialFingerprint, StringComparison.Ordinal))
            {
                return Problem(context, 403, "Approved preview required",
                    "Preview this exact provider and supply the same credential before applying.", "policydenied");
            }

            var probed = await discovery.ProbeAsync(new ModelConnectionProbeRequest(
                approved.Candidate.Endpoint,
                approved.Candidate.ProviderType,
                approved.Candidate.Model,
                credential), cancellationToken);
            if (!probed.IsSuccess)
            {
                return DomainProblem(context, probed.Failure!, "Provider connection verification failed");
            }
            var administratorCredential = await MaterializeAdministratorCredentialAsync(
                session.InstallationId, administrators, secretStore, cancellationToken);
            if (!administratorCredential.IsSuccess)
            {
                return DomainProblem(context, administratorCredential.Failure!, "Provider creation failed");
            }

            DomainResult<ProviderCreateResult> created;
            await using (var lease = administratorCredential.Value)
            {
                created = await editor.CreateProviderAsync(new ApplyProviderCreateRequest(
                    approved.ExpectedInstallationVersion,
                    approved.Candidate,
                    credential,
                    approved.RequestHash,
                    session.ActorId,
                    approved.CorrelationId,
                    lease.Value), cancellationToken);
            }
            if (!created.IsSuccess)
            {
                return DomainProblem(context, created.Failure!, "Provider creation failed");
            }

            var response = new
            {
                installationVersion = created.Value.Installation.Version,
                provider = new
                {
                    id = created.Value.Provider.Id.Value,
                    created.Value.Provider.Name,
                    created.Value.Provider.ProviderType,
                    endpoint = created.Value.Provider.Endpoint.AbsoluteUri,
                    created.Value.Provider.Model,
                    created.Value.Provider.Version,
                    authentication = created.Value.Provider.SecretReference.IsNoCredential
                        ? "No API key"
                        : "OS-backed secret",
                },
                changes = created.Value.Changes.Select(ChangeResponse),
                previewHash = created.Value.RequestHash,
                correlationId = approved.CorrelationId.Value,
            };
            session.ProviderCreatePreviews.TryRemove(request.PreviewHash, out _);
            StoreIdempotentResult(session, scopedKey, webHash, response);
            return Results.Ok(response);
        }
        finally
        {
            session.MutationGate.Release();
        }
    }

    private static async Task<IResult> PreviewAgentCreateAsync(
        ReadyAgentCreateWebRequest request,
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
        if (acquired.Failure is not null) return acquired.Failure;
        var candidate = AgentCandidate(request);
        if (!candidate.IsSuccess)
        {
            return DomainProblem(context, candidate.Failure!, "Agent creation preview failed");
        }

        var session = acquired.Session!;
        var idempotencyKey = context.Request.Headers["Idempotency-Key"].ToString();
        var webHash = SnapshotHash(request);
        var scopedKey = $"agent-create-preview:{idempotencyKey}";
        await session.MutationGate.WaitAsync(cancellationToken);
        try
        {
            if (session.Results.TryGetValue(scopedKey, out var existing))
            {
                return string.Equals(existing.RequestHash, webHash, StringComparison.Ordinal)
                    ? Replay(context, existing.Response)
                    : Problem(context, 409, "Idempotency conflict",
                        "The idempotency key is already bound to another agent preview.", "idempotency-conflict");
            }
            var administratorCredential = await MaterializeAdministratorCredentialAsync(
                session.InstallationId, administrators, secretStore, cancellationToken);
            if (!administratorCredential.IsSuccess)
            {
                return DomainProblem(context, administratorCredential.Failure!, "Agent creation preview failed");
            }

            var stable = StableRequestIdentity(session.InstallationId, "agent-create-preview", idempotencyKey);
            var correlation = new CorrelationId($"admin-agent-create:{Convert.ToHexStringLower(stable.AsSpan(0, 16))}");
            DomainResult<AgentCreatePreview> preview;
            await using (var lease = administratorCredential.Value)
            {
                preview = await editor.PreviewAgentCreateAsync(new PreviewAgentCreateRequest(
                    request.ExpectedInstallationVersion,
                    request.ExpectedProviderVersion,
                    candidate.Value,
                    session.ActorId,
                    correlation,
                    lease.Value), cancellationToken);
            }
            if (!preview.IsSuccess)
            {
                return DomainProblem(context, preview.Failure!, "Agent creation preview failed");
            }

            RetainBoundedPreviews(session.AgentCreatePreviews, 7);
            session.AgentCreatePreviews[preview.Value.RequestHash] = new ReadyAgentCreatePreview(
                request.ExpectedInstallationVersion,
                request.ExpectedProviderVersion,
                candidate.Value,
                correlation,
                preview.Value.RequestHash);
            var response = new
            {
                previewHash = preview.Value.RequestHash,
                changes = preview.Value.Changes.Select(ChangeResponse),
                effective = EffectivePolicyResponse(preview.Value.Effective),
                impact = new
                {
                    newAgentVersion = 0,
                    providerVersion = request.ExpectedProviderVersion,
                    existingRunsChanged = false,
                    authorityStartsInactive = false,
                },
                warning = "The new identity receives exactly the reviewed policy; it inherits no authority from another agent.",
                correlationId = correlation.Value,
            };
            StoreIdempotentResult(session, scopedKey, webHash, response);
            return Results.Ok(response);
        }
        finally
        {
            session.MutationGate.Release();
        }
    }

    private static async Task<IResult> ApplyAgentCreateAsync(
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
        if (acquired.Failure is not null) return acquired.Failure;

        var session = acquired.Session!;
        var idempotencyKey = context.Request.Headers["Idempotency-Key"].ToString();
        var webHash = SnapshotHash(new { request.PreviewHash });
        var scopedKey = $"agent-create-apply:{idempotencyKey}";
        await session.MutationGate.WaitAsync(cancellationToken);
        try
        {
            if (session.Results.TryGetValue(scopedKey, out var existing))
            {
                return string.Equals(existing.RequestHash, webHash, StringComparison.Ordinal)
                    ? Replay(context, existing.Response)
                    : Problem(context, 409, "Idempotency conflict",
                        "The idempotency key is already bound to another agent creation.", "idempotency-conflict");
            }
            if (!Text(request.PreviewHash, 128) ||
                !session.AgentCreatePreviews.TryGetValue(request.PreviewHash, out var approved))
            {
                return Problem(context, 403, "Approved preview required",
                    "Preview the exact agent policy in this operator session before applying it.", "policydenied");
            }
            var administratorCredential = await MaterializeAdministratorCredentialAsync(
                session.InstallationId, administrators, secretStore, cancellationToken);
            if (!administratorCredential.IsSuccess)
            {
                return DomainProblem(context, administratorCredential.Failure!, "Agent creation failed");
            }

            DomainResult<AgentCreateResult> created;
            await using (var lease = administratorCredential.Value)
            {
                created = await editor.CreateAgentAsync(new ApplyAgentCreateRequest(
                    approved.ExpectedInstallationVersion,
                    approved.ExpectedProviderVersion,
                    approved.Candidate,
                    approved.RequestHash,
                    session.ActorId,
                    approved.CorrelationId,
                    lease.Value), cancellationToken);
            }
            if (!created.IsSuccess)
            {
                return DomainProblem(context, created.Failure!, "Agent creation failed");
            }

            var response = new
            {
                installationVersion = created.Value.Installation.Version,
                agent = AgentEditResponse(created.Value.Agent),
                effective = EffectivePolicyResponse(created.Value.Effective),
                changes = created.Value.Changes.Select(ChangeResponse),
                previewHash = created.Value.RequestHash,
                correlationId = approved.CorrelationId.Value,
            };
            session.AgentCreatePreviews.TryRemove(request.PreviewHash, out _);
            StoreIdempotentResult(session, scopedKey, webHash, response);
            return Results.Ok(response);
        }
        finally
        {
            session.MutationGate.Release();
        }
    }

    private static DomainResult<AgentIdentityCandidate> AgentCandidate(ReadyAgentCreateWebRequest request)
    {
        if (request.PrimaryProviderId == Guid.Empty ||
            !TryEnum(request.DataLocality, out ModelDataLocality dataLocality) ||
            !TryEnum(request.MemoryScope, out AgentMemoryScope memoryScope) ||
            !TryEnum(request.NetworkPosture, out NetworkPosture networkPosture) ||
            !TryEnum(request.LearningMode, out LearningMode learningMode) ||
            !TryEnum(request.MutableSkillScope, out MutableSkillScope mutableSkillScope))
        {
            return DomainResult.Fail<AgentIdentityCandidate>(new DomainFailure(
                FailureCode.ValidationFailure,
                "Agent policy contains an invalid provider or enum selection."));
        }

        return DomainResult.Success(new AgentIdentityCandidate(
            request.Name,
            request.Expertise,
            request.Mission,
            request.PreferredLanguage,
            request.TimeZone,
            request.ResponseStyle,
            request.DefaultWorkspace,
            new AgentModelPolicy(new ProviderProfileId(request.PrimaryProviderId), dataLocality, request.AllowFallback),
            new AgentMemoryPolicy(memoryScope, request.RetentionDays),
            new AgentCapabilityPolicy(
                networkPosture,
                request.ToolGrants ?? [],
                request.SkillGrants ?? []),
            new AgentBudget(
                request.MaxTurns,
                request.MaxToolInvocations,
                request.MaxInputTokens,
                request.MaxOutputTokens,
                request.MaxWallClockSeconds)
            {
                ContextWindowOverrideTokens = request.ContextWindowOverrideTokens,
                ContextCompressionEnabled = request.ContextCompressionEnabled ?? true,
                ContextCompressionThresholdPercent = request.ContextCompressionThresholdPercent ?? 80,
                ContextCompressionTargetPercent = request.ContextCompressionTargetPercent ?? 50,
                ContextProtectedRecentTurns = request.ContextProtectedRecentTurns ?? 4,
            },
            new ChildAgentLimits(
                request.MaxChildDepth,
                request.MaxChildren,
                request.MaxChildConcurrency,
                request.MaxChildTokens),
            new AgentLearningPolicy(learningMode, mutableSkillScope)));
    }

    private static bool TryEnum<T>(string? value, out T result) where T : struct, Enum =>
        Enum.TryParse(value, ignoreCase: true, out result) && Enum.IsDefined(result);

    private static string CredentialFingerprint(ReadOnlySpan<char> credential)
    {
        if (credential.IsEmpty) return "none";
        var characters = credential.ToArray();
        var bytes = Encoding.UTF8.GetBytes(characters);
        try
        {
            return Convert.ToHexStringLower(SHA256.HashData(bytes));
        }
        finally
        {
            Array.Clear(characters);
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static object EffectivePolicyResponse(EffectiveAgentDefinition effective) => new
    {
        effective.ProviderName,
        effective.Model,
        effective.ProviderCapabilities,
        agent = new
        {
            effective.Agent.ModelPolicy,
            effective.Agent.MemoryPolicy,
            effective.Agent.CapabilityPolicy,
            effective.Agent.Budget,
            effective.Agent.ChildLimits,
            effective.Agent.LearningPolicy,
        },
        capabilities = effective.Capabilities.Select(item => new
        {
            item.CapabilityId,
            decision = item.Decision.ToString(),
            item.Reason,
        }),
    };
}
