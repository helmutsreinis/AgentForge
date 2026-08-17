using System.Collections.Immutable;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentForge.Abstractions.Agents;
using AgentForge.Abstractions.Installations;
using AgentForge.Abstractions.Models;
using AgentForge.Abstractions.Providers;
using AgentForge.Abstractions.Setup;
using AgentForge.Abstractions.Time;
using AgentForge.Domain.Agents;
using AgentForge.Domain.Installations;
using AgentForge.Domain.Models;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Providers;
using AgentForge.Domain.Security;
using AgentForge.Domain.Setup;

namespace AgentForge.Host.Setup;

internal sealed record WebProviderRequest(string Name, string ProviderType, string Endpoint, string Model);
internal sealed record WebProviderConnectionRequest(string Name, string ProviderType, string Endpoint);
internal sealed record WebManualModelRequest(string Name, string ProviderType, string Endpoint, string Model);
internal sealed record WebAgentRequest(
    string Name,
    string? Expertise,
    string? Mission,
    string PreferredLanguage,
    string TimeZone,
    string ResponseStyle,
    string? DefaultWorkspace);

internal static class WebSetupEndpoints
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static IEndpointRouteBuilder MapAgentForgeWebSetup(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/setup/web");
        group.MapGet("/session", GetSession);
        group.MapPost("/session", CreateSessionAsync);
        group.MapPost("/begin", BeginAsync);
        group.MapPost("/provider/discover", PrepareDiscovery);
        group.MapPost("/provider/models", DiscoverModelsAsync);
        group.MapPost("/provider/model/manual", UseManualModel);
        group.MapPost("/provider/select", SelectModel);
        group.MapPost("/provider/test", TestModelAsync);
        group.MapPost("/provider", PrepareProvider);
        group.MapPost("/provider/credential", ConfigureProviderAsync);
        group.MapPost("/agent/preview", PreviewAgentAsync);
        group.MapPost("/agent", CreateAgentAsync);
        group.MapPost("/complete", CompleteAsync);
        return endpoints;
    }

    private static IResult GetSession(HttpContext context, WebSetupSessionManager sessions, IClock clock)
    {
        if (!IsTrustedLoopback(context)) return Problem(403, "Loopback required", "Web setup is available only from loopback.");
        var token = context.Request.Cookies[WebSetupSessionManager.CookieName];
        var session = sessions.Validate(token, null, clock.UtcNow, false);
        if (session is null)
            return Problem(404, "No setup session", "Start a protected setup session from this loopback browser.");
        return session.Completed
            ? Problem(409, "Setup complete", "AgentForge is already configured and Ready.")
            : Results.Ok(SessionResponse(session, resumed: true));
    }

    private static async Task<IResult> CreateSessionAsync(
        HttpContext context,
        WebSetupSessionManager sessions,
        IClock clock,
        IInstallationRepository installations,
        IProviderProfileRepository providerProfiles,
        IAgentIdentityRepository agents,
        CancellationToken cancellationToken)
    {
        if (!IsTrustedLoopback(context)) return Problem(403, "Loopback required", "Web setup is available only from loopback.");
        var installation = await installations.ReadAsync(cancellationToken);
        if (installation.State is InstallationState.Ready)
            return Problem(409, "Setup complete", "AgentForge is already configured and Ready.");

        var existingToken = context.Request.Cookies[WebSetupSessionManager.CookieName];
        var created = sessions.CreateOrResume(existingToken, clock.UtcNow);
        if (created is null)
            return Problem(409, "Setup already active", "Continue setup in the browser that started it, or retry after that session expires.");

        if (!created.Resumed)
        {
            created.Session.Begun = installation.State is InstallationState.Configuring;
            if (installation.Id.Value != Guid.Empty)
            {
                var profiles = await providerProfiles.ListAsync(installation.Id, cancellationToken);
                if (profiles.Count > 0)
                {
                    var profile = profiles.OrderBy(item => item.CreatedAt).First();
                    created.Session.ProviderId = profile.Id;
                    created.Session.PendingProvider = new PendingWebProvider(
                        profile.Name, profile.ProviderType, profile.Endpoint, profile.Model);
                    created.Session.AvailableModels = [profile.Model];
                    created.Session.ModelTested = true;
                }

                created.Session.AgentCreated = (await agents.ListAsync(installation.Id, cancellationToken)).Count > 0;
            }
        }

        context.Response.Cookies.Append(WebSetupSessionManager.CookieName, created.Token, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            Secure = context.Request.IsHttps,
            MaxAge = TimeSpan.FromMinutes(30),
            Path = "/api/v1/setup/web",
            IsEssential = true,
        });
        return Results.Ok(SessionResponse(created.Session, created.Resumed));
    }

    private static async Task<IResult> UseManualModel(
        HttpContext context,
        WebManualModelRequest request,
        WebSetupSessionManager sessions,
        CancellationToken cancellationToken)
    {
        var acquired = await AcquireMutationSessionAsync(context, sessions, cancellationToken);
        if (acquired.Failure is not null) return acquired.Failure;
        try
        {
            var validation = ValidateMutationLocked(context, acquired.Session!, "provider-model-manual", RequestHash(request));
            if (validation.Failure is not null) return validation.Failure;
            if (validation.Replay is not null) return Results.Ok(validation.Replay);
            var pending = validation.Session!.PendingProvider;
            if (pending is null || request is null || !Text(request.Model, 256) ||
                !string.Equals(pending.Name, request.Name, StringComparison.Ordinal) ||
                !string.Equals(pending.ProviderType, request.ProviderType, StringComparison.Ordinal) ||
                !string.Equals(pending.Endpoint.ToString(), request.Endpoint, StringComparison.Ordinal))
                return Problem(400, "Invalid model", "Enter a model ID for the provider endpoint prepared in this setup session.");

            var model = request.Model.Trim();
            validation.Session.AvailableModels = [model];
            validation.Session.PendingProvider = pending with { Model = null };
            validation.Session.ModelTested = false;
            var response = new { accepted = true, model, source = "manual-entry" };
            Store(validation, response);
            return Results.Ok(response);
        }
        finally { acquired.Session!.MutationGate.Release(); }
    }

    private static object SessionResponse(WebSetupSession session, bool resumed) => new
    {
        csrfToken = session.CsrfToken,
        expiresAtUtc = session.ExpiresAtUtc,
        resumed,
        begun = session.Begun,
        providerConfigured = session.ProviderId is not null,
        modelTested = session.ModelTested,
        agentCreated = session.AgentCreated,
        completed = session.Completed,
        currentStep = CurrentStep(session),
        provider = session.PendingProvider is null ? null : new
        {
            session.PendingProvider.Name,
            session.PendingProvider.ProviderType,
            endpoint = session.PendingProvider.Endpoint.ToString(),
            session.PendingProvider.Model,
        },
        models = session.AvailableModels,
    };

    private static int CurrentStep(WebSetupSession session) => session.Completed ? 5
        : session.AgentCreated ? 5
        : session.ModelTested ? 4
        : session.PendingProvider?.Model is not null ? 3
        : session.AvailableModels.Count > 0 ? 2
        : 1;

    private static async Task<IResult> BeginAsync(
        HttpContext context,
        WebSetupSessionManager sessions,
        ISetupApplicationService setup,
        IInstallationStateReader stateReader,
        CancellationToken cancellationToken)
    {
        var acquired = await AcquireMutationSessionAsync(context, sessions, cancellationToken);
        if (acquired.Failure is not null) return acquired.Failure;
        try
        {
            var validation = ValidateMutationLocked(context, acquired.Session!, "begin", "begin");
            if (validation.Failure is not null) return validation.Failure;
            if (validation.Replay is not null) return Results.Ok(validation.Replay);
            if (validation.Session!.Begun)
            {
                var current = await stateReader.ReadAsync(cancellationToken);
                var resumedResponse = new { installationId = current.Id.Value, state = current.State.ToString(), version = current.Version, resumed = true };
                Store(validation, resumedResponse);
                return Results.Ok(resumedResponse);
            }
            var actor = Actor(validation.Session!);
            var result = await setup.BeginAsync(new BeginSetupRequest(null, actor, Correlation(context)), cancellationToken);
            if (!result.IsSuccess) return DomainProblem(result.Failure!);
            var response = new { installationId = result.Value.Installation.Id.Value, state = result.Value.Installation.State.ToString(), version = result.Value.Installation.Version };
            Store(validation, response);
            validation.Session.Begun = true;
            return Results.Ok(response);
        }
        finally { acquired.Session!.MutationGate.Release(); }
    }

    private static async Task<IResult> PrepareDiscovery(
        HttpContext context,
        WebProviderConnectionRequest request,
        WebSetupSessionManager sessions,
        CancellationToken cancellationToken)
    {
        var acquired = await AcquireMutationSessionAsync(context, sessions, cancellationToken);
        if (acquired.Failure is not null) return acquired.Failure;
        try
        {
            var validation = ValidateMutationLocked(context, acquired.Session!, "provider-discovery-prepare", RequestHash(request));
            if (validation.Failure is not null) return validation.Failure;
            if (validation.Replay is not null) return Results.Ok(validation.Replay);
            if (request is null || !Uri.TryCreate(request.Endpoint, UriKind.Absolute, out var endpoint) ||
                !Text(request.Name, 128) || !Text(request.ProviderType, 64))
                return Problem(400, "Invalid provider", "Enter a provider name, adapter type, and absolute base endpoint.");
            validation.Session!.PendingProvider = new PendingWebProvider(
                request.Name.Trim(), request.ProviderType.Trim(), endpoint, null);
            validation.Session.AvailableModels = [];
            validation.Session.ModelTested = false;
            var response = new { prepared = true, provider = request.Name.Trim(), endpoint = endpoint.ToString() };
            Store(validation, response);
            return Results.Ok(response);
        }
        finally { acquired.Session!.MutationGate.Release(); }
    }

    private static async Task<IResult> DiscoverModelsAsync(
        HttpContext context,
        WebSetupSessionManager sessions,
        IModelCatalogDiscoveryService discovery,
        CancellationToken cancellationToken)
    {
        var acquired = await AcquireMutationSessionAsync(context, sessions, cancellationToken);
        if (acquired.Failure is not null) return acquired.Failure;
        try
        {
            var validation = ValidateMutationLocked(context, acquired.Session!, "provider-models", "credential-redacted");
            if (validation.Failure is not null) return validation.Failure;
            if (validation.Replay is not null) return Results.Ok(validation.Replay);
            if (validation.Session!.PendingProvider is not { } pending)
                return Problem(400, "Provider required", "Prepare a provider endpoint before discovering models.");
            var credential = await ReadCredentialAsync(context, allowEmpty: true, cancellationToken);
            if (credential.Failure is not null) return credential.Failure;
            try
            {
                var result = await discovery.DiscoverAsync(new ModelCatalogDiscoveryRequest(
                    pending.Endpoint, pending.ProviderType, credential.Value), cancellationToken);
                if (!result.IsSuccess) return DomainProblem(result.Failure!);
                var models = result.Value.Models.Select(model => model.Id).ToArray();
                validation.Session.AvailableModels = models;
                var response = new
                {
                    models = result.Value.Models.Select(model => new { model.Id, model.OwnedBy, model.MaximumContextTokens }),
                    catalogEndpoint = result.Value.CatalogEndpoint.ToString(),
                    result.Value.ObservedAtUtc,
                };
                Store(validation, response);
                return Results.Ok(response);
            }
            finally { Array.Clear(credential.Value); }
        }
        finally { acquired.Session!.MutationGate.Release(); }
    }

    private static async Task<IResult> SelectModel(
        HttpContext context,
        WebProviderRequest request,
        WebSetupSessionManager sessions,
        CancellationToken cancellationToken)
    {
        var acquired = await AcquireMutationSessionAsync(context, sessions, cancellationToken);
        if (acquired.Failure is not null) return acquired.Failure;
        try
        {
            var validation = ValidateMutationLocked(context, acquired.Session!, "provider-select", RequestHash(request));
            if (validation.Failure is not null) return validation.Failure;
            if (validation.Replay is not null) return Results.Ok(validation.Replay);
            var pending = validation.Session!.PendingProvider;
            if (pending is null || request is null || !Text(request.Model, 256) ||
                !validation.Session.AvailableModels.Contains(request.Model, StringComparer.Ordinal) ||
                !string.Equals(pending.Name, request.Name, StringComparison.Ordinal) ||
                !string.Equals(pending.ProviderType, request.ProviderType, StringComparison.Ordinal) ||
                !string.Equals(pending.Endpoint.ToString(), request.Endpoint, StringComparison.Ordinal))
                return Problem(400, "Invalid model", "Select a model prepared for the current provider endpoint.");
            validation.Session.PendingProvider = pending with { Model = request.Model };
            validation.Session.ModelTested = false;
            var response = new { selected = true, model = request.Model };
            Store(validation, response);
            return Results.Ok(response);
        }
        finally { acquired.Session!.MutationGate.Release(); }
    }

    private static async Task<IResult> TestModelAsync(
        HttpContext context,
        WebSetupSessionManager sessions,
        IModelCatalogDiscoveryService discovery,
        CancellationToken cancellationToken)
    {
        var acquired = await AcquireMutationSessionAsync(context, sessions, cancellationToken);
        if (acquired.Failure is not null) return acquired.Failure;
        try
        {
            var validation = ValidateMutationLocked(context, acquired.Session!, "provider-test", "credential-redacted");
            if (validation.Failure is not null) return validation.Failure;
            if (validation.Replay is not null) return Results.Ok(validation.Replay);
            if (validation.Session!.PendingProvider is not { Model: { } model } pending)
                return Problem(400, "Model required", "Select a prepared model before testing it.");
            var credential = await ReadCredentialAsync(context, allowEmpty: true, cancellationToken);
            if (credential.Failure is not null) return credential.Failure;
            try
            {
                var result = await discovery.ProbeAsync(new ModelConnectionProbeRequest(
                    pending.Endpoint, pending.ProviderType, model, credential.Value), cancellationToken);
                if (!result.IsSuccess) return DomainProblem(result.Failure!);
                validation.Session.ModelTested = true;
                var response = new
                {
                    tested = true,
                    result.Value.Model,
                    durationMilliseconds = Math.Max(0, (long)result.Value.Duration.TotalMilliseconds),
                    result.Value.Evidence,
                };
                Store(validation, response);
                return Results.Ok(response);
            }
            finally { Array.Clear(credential.Value); }
        }
        finally { acquired.Session!.MutationGate.Release(); }
    }

    private static async Task<IResult> PrepareProvider(
        HttpContext context,
        WebProviderRequest request,
        WebSetupSessionManager sessions,
        CancellationToken cancellationToken)
    {
        var acquired = await AcquireMutationSessionAsync(context, sessions, cancellationToken);
        if (acquired.Failure is not null) return acquired.Failure;
        try
        {
            var hash = RequestHash(request);
            var validation = ValidateMutationLocked(context, acquired.Session!, "provider-prepare", hash);
            if (validation.Failure is not null) return validation.Failure;
            if (validation.Replay is not null) return Results.Ok(validation.Replay);
            if (request is null || !Uri.TryCreate(request.Endpoint, UriKind.Absolute, out var endpoint) ||
                !Text(request.Name, 128) || !Text(request.ProviderType, 64) || !Text(request.Model, 256))
                return Problem(400, "Invalid provider", "Provider fields are invalid.");
            validation.Session!.PendingProvider = new PendingWebProvider(
                request.Name.Trim(), request.ProviderType.Trim(), endpoint, request.Model.Trim());
            var response = new { prepared = true, provider = request.Name.Trim() };
            Store(validation, response);
            return Results.Ok(response);
        }
        finally { acquired.Session!.MutationGate.Release(); }
    }

    private static async Task<IResult> ConfigureProviderAsync(
        HttpContext context,
        WebSetupSessionManager sessions,
        ISetupApplicationService setup,
        CancellationToken cancellationToken)
    {
        var acquired = await AcquireMutationSessionAsync(context, sessions, cancellationToken);
        if (acquired.Failure is not null) return acquired.Failure;
        try
        {
            var validation = ValidateMutationLocked(context, acquired.Session!, "provider-credential", "credential");
            if (validation.Failure is not null) return validation.Failure;
            if (validation.Replay is not null) return Results.Ok(validation.Replay);
            if (context.Request.ContentType?.StartsWith("text/plain", StringComparison.OrdinalIgnoreCase) != true ||
                context.Request.ContentLength is null or < 0 or > 8192 ||
                validation.Session!.PendingProvider is not { Model: { } model } ||
                !validation.Session.ModelTested &&
                    !string.Equals(validation.Session.PendingProvider.ProviderType, "deterministic", StringComparison.OrdinalIgnoreCase))
                return Problem(400, "Invalid credential request", "Test a selected model and submit its bounded credential.");
            var bytes = new byte[(int)context.Request.ContentLength.Value];
            var read = 0;
            while (read < bytes.Length)
            {
                var count = await context.Request.Body.ReadAsync(bytes.AsMemory(read), cancellationToken);
                if (count == 0) break;
                read += count;
            }
            if (read != bytes.Length) { Array.Clear(bytes); return Problem(400, "Invalid credential", "Credential body is incomplete."); }
            char[] credential;
            try { credential = StrictUtf8.GetChars(bytes); }
            catch (DecoderFallbackException) { Array.Clear(bytes); return Problem(400, "Invalid credential", "Credential encoding is invalid."); }
            Array.Clear(bytes);
            try
            {
                var pending = validation.Session.PendingProvider;
                var result = await setup.ConfigureProviderCredentialAsync(new ConfigureProviderCredentialRequest(
                    pending.Name, pending.ProviderType, pending.Endpoint, model, credential,
                    Actor(validation.Session), Correlation(context)), cancellationToken);
                if (!result.IsSuccess) return DomainProblem(result.Failure!);
                validation.Session.ProviderId = result.Value.Profile.Id;
                var response = new { providerId = result.Value.Profile.Id.Value, result.Value.Profile.Name, result.Value.Profile.Model };
                Store(validation, response);
                return Results.Ok(response);
            }
            finally { Array.Clear(credential); }
        }
        finally { acquired.Session!.MutationGate.Release(); }
    }

    private static async Task<IResult> PreviewAgentAsync(
        HttpContext context,
        WebAgentRequest request,
        WebSetupSessionManager sessions,
        ISetupApplicationService setup,
        CancellationToken cancellationToken)
    {
        var acquired = await AcquireMutationSessionAsync(context, sessions, cancellationToken);
        if (acquired.Failure is not null) return acquired.Failure;
        try
        {
            var validation = ValidateMutationLocked(context, acquired.Session!, "agent-preview", RequestHash(request));
            if (validation.Failure is not null) return validation.Failure;
            if (validation.Replay is not null) return Results.Ok(validation.Replay);
            var candidate = Candidate(request, validation.Session?.ProviderId);
            if (candidate is null) return Problem(400, "Invalid agent", "Configure a provider and submit bounded agent fields.");
            var result = await setup.PreviewAgentAsync(new PreviewAgentRequest(
                candidate, Actor(validation.Session!), Correlation(context)), cancellationToken);
            if (!result.IsSuccess) return DomainProblem(result.Failure!);
            var response = new
            {
                agent = result.Value.Agent.Name,
                provider = result.Value.ProviderName,
                model = result.Value.Model,
                capabilities = result.Value.Capabilities.Select(item => new { item.CapabilityId, decision = item.Decision.ToString(), item.Reason }),
            };
            Store(validation, response);
            return Results.Ok(response);
        }
        finally { acquired.Session!.MutationGate.Release(); }
    }

    private static async Task<IResult> CreateAgentAsync(
        HttpContext context,
        WebAgentRequest request,
        WebSetupSessionManager sessions,
        ISetupApplicationService setup,
        CancellationToken cancellationToken)
    {
        var acquired = await AcquireMutationSessionAsync(context, sessions, cancellationToken);
        if (acquired.Failure is not null) return acquired.Failure;
        try
        {
            var validation = ValidateMutationLocked(context, acquired.Session!, "agent-create", RequestHash(request));
            if (validation.Failure is not null) return validation.Failure;
            if (validation.Replay is not null) return Results.Ok(validation.Replay);
            var candidate = Candidate(request, validation.Session?.ProviderId);
            if (candidate is null) return Problem(400, "Invalid agent", "Configure a provider and submit bounded agent fields.");
            var result = await setup.CreateAgentAsync(new CreateAgentRequest(
                candidate, Actor(validation.Session!), Correlation(context)), cancellationToken);
            if (!result.IsSuccess) return DomainProblem(result.Failure!);
            var response = new { agentId = result.Value.Agent.Id.Value, result.Value.Agent.Name, version = result.Value.Agent.Version };
            Store(validation, response);
            validation.Session!.AgentCreated = true;
            return Results.Ok(response);
        }
        finally { acquired.Session!.MutationGate.Release(); }
    }

    private static async Task<IResult> CompleteAsync(
        HttpContext context,
        WebSetupSessionManager sessions,
        ISetupApplicationService setup,
        CancellationToken cancellationToken)
    {
        var acquired = await AcquireMutationSessionAsync(context, sessions, cancellationToken);
        if (acquired.Failure is not null) return acquired.Failure;
        try
        {
            var validation = ValidateMutationLocked(context, acquired.Session!, "complete", "complete");
            if (validation.Failure is not null) return validation.Failure;
            if (validation.Replay is not null) return Results.Ok(validation.Replay);
            var result = await setup.CompleteAsync(new CompleteSetupRequest(
                Actor(validation.Session!), Correlation(context)), cancellationToken);
            if (!result.IsSuccess) return DomainProblem(result.Failure!);
            var response = new
            {
                installationId = result.Value.Installation.Id.Value,
                state = result.Value.Installation.State.ToString(),
                checks = result.Value.Checks.Select(item => new { item.CheckId, item.Succeeded, item.Summary }),
                administratorCredentialReference = result.Value.Administrator.ClientCredentialReference,
            };
            Store(validation, response);
            validation.Session!.Completed = true;
            return Results.Ok(response);
        }
        finally { acquired.Session!.MutationGate.Release(); }
    }

    private static AgentIdentityCandidate? Candidate(WebAgentRequest request, ProviderProfileId? providerId)
    {
        if (request is null || providerId is null || !Text(request.Name, 128) ||
            !Text(request.PreferredLanguage, 35) || !Text(request.TimeZone, 128) ||
            !Text(request.ResponseStyle, 512) || !Optional(request.Expertise, 512) ||
            !Optional(request.Mission, 4096) || !Optional(request.DefaultWorkspace, 1024)) return null;
        return new AgentIdentityCandidate(
            request.Name.Trim(), request.Expertise?.Trim(), request.Mission?.Trim(),
            request.PreferredLanguage.Trim(), request.TimeZone.Trim(), request.ResponseStyle.Trim(),
            request.DefaultWorkspace?.Trim(),
            new AgentModelPolicy(providerId.Value, ModelDataLocality.LocalOnly, false),
            new AgentMemoryPolicy(AgentMemoryScope.Agent, 30),
            new AgentCapabilityPolicy(NetworkPosture.Denied, [], []),
            new AgentBudget(64, 0, 16_000, 4_000, 3600),
            new ChildAgentLimits(0, 0, 0, 0),
            new AgentLearningPolicy(LearningMode.Propose, MutableSkillScope.ProposalWorkspaceOnly));
    }

    private static async ValueTask<WebSessionAcquisition> AcquireMutationSessionAsync(
        HttpContext context,
        WebSetupSessionManager sessions,
        CancellationToken cancellationToken)
    {
        if (!IsTrustedLoopback(context))
            return new(null, Problem(403, "Loopback required", "Web setup is available only from loopback."));
        var token = context.Request.Cookies[WebSetupSessionManager.CookieName];
        var csrf = context.Request.Headers["X-CSRF-Token"].ToString();
        var clock = context.RequestServices.GetRequiredService<IClock>();
        var session = sessions.Validate(token, csrf, clock.UtcNow, true);
        if (session is null)
            return new(null, Problem(401, "Invalid session", "The setup session or CSRF token is invalid."));
        await session.MutationGate.WaitAsync(cancellationToken);
        return new(session, null);
    }

    private static WebMutationValidation ValidateMutationLocked(
        HttpContext context, WebSetupSession session, string operation, string requestHash)
    {
        var key = context.Request.Headers["Idempotency-Key"].ToString();
        if (!Text(key, 128)) return new(session, null, Problem(400, "Idempotency required", "A bounded Idempotency-Key is required."), null, operation, requestHash);
        var scopedKey = $"{operation}:{key}";
        if (session.Results.TryGetValue(scopedKey, out var existing))
        {
            return existing.RequestHash == requestHash
                ? new(session, scopedKey, null, existing.Response, operation, requestHash)
                : new(session, scopedKey, Problem(409, "Idempotency conflict", "The key is bound to different input."), null, operation, requestHash);
        }
        if (session.Completed)
            return new(session, scopedKey, Problem(409, "Setup complete", "The completed setup session accepts exact replays only."), null, operation, requestHash);
        return new(session, scopedKey, null, null, operation, requestHash);
    }

    private static void Store(WebMutationValidation validation, object response) =>
        validation.Session!.Results.TryAdd(validation.ScopedKey!, new WebIdempotencyResult(validation.RequestHash, response));

    private static async Task<WebCredentialRead> ReadCredentialAsync(
        HttpContext context,
        bool allowEmpty,
        CancellationToken cancellationToken)
    {
        if (context.Request.ContentType?.StartsWith("text/plain", StringComparison.OrdinalIgnoreCase) != true ||
            context.Request.ContentLength is null or < 0 or > 8192 ||
            !allowEmpty && context.Request.ContentLength == 0)
        {
            return new([], Problem(400, "Invalid credential", "Submit a bounded plain-text provider credential."));
        }

        var bytes = new byte[(int)context.Request.ContentLength.Value];
        var read = 0;
        while (read < bytes.Length)
        {
            var count = await context.Request.Body.ReadAsync(bytes.AsMemory(read), cancellationToken);
            if (count == 0) break;
            read += count;
        }

        if (read != bytes.Length)
        {
            Array.Clear(bytes);
            return new([], Problem(400, "Invalid credential", "The credential body is incomplete."));
        }

        try
        {
            return new(StrictUtf8.GetChars(bytes), null);
        }
        catch (DecoderFallbackException)
        {
            return new([], Problem(400, "Invalid credential", "The credential encoding is invalid."));
        }
        finally
        {
            Array.Clear(bytes);
        }
    }

    private static bool IsTrustedLoopback(HttpContext context)
    {
        var address = context.Connection.RemoteIpAddress;
        if (address is not null && !IPAddress.IsLoopback(address)) return false;
        var origin = context.Request.Headers.Origin.ToString();
        if (string.IsNullOrEmpty(origin)) return true;
        return Uri.TryCreate(origin, UriKind.Absolute, out var uri) &&
            string.Equals(uri.Scheme, context.Request.Scheme, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(uri.Authority, context.Request.Host.Value, StringComparison.OrdinalIgnoreCase);
    }

    private static ActorId Actor(WebSetupSession session) => new($"web-setup:{session.Hash[7..23]}");
    private static CorrelationId Correlation(HttpContext context) => new(context.TraceIdentifier);
    private static string RequestHash<T>(T value) => WebSetupSessionManager.Hash(JsonSerializer.Serialize(value));
    private static bool Text(string value, int max) => !string.IsNullOrWhiteSpace(value) && value.Length <= max && !value.Any(char.IsControl);
    private static bool Optional(string? value, int max) => value is null || value.Length <= max && !value.Any(character => character == '\0');
    private static IResult DomainProblem(DomainFailure failure) => Problem(failure.Code switch
    {
        FailureCode.PolicyDenied => 403,
        FailureCode.ApprovalRequired => 409,
        FailureCode.ConcurrencyConflict => 409,
        FailureCode.UnsupportedCapability => 501,
        FailureCode.RecoverableExternalFailure => 503,
        _ => 400,
    }, failure.Code.ToString(), failure.Message);
    private static IResult Problem(int status, string title, string detail) => Results.Problem(
        statusCode: status, title: title, detail: detail, type: $"urn:agentforge:problem:web-setup:{status}");

    private sealed record WebMutationValidation(
        WebSetupSession? Session,
        string? ScopedKey,
        IResult? Failure,
        object? Replay,
        string Operation,
        string RequestHash);

    private sealed record WebSessionAcquisition(WebSetupSession? Session, IResult? Failure);
    private sealed record WebCredentialRead(char[] Value, IResult? Failure);
}
