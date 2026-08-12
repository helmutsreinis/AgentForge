using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentForge.Abstractions.Agents;
using AgentForge.Abstractions.Installations;
using AgentForge.Abstractions.Orchestration;
using AgentForge.Abstractions.Security;
using AgentForge.Abstractions.Skills;
using AgentForge.Abstractions.Time;
using AgentForge.Domain.Agents;
using AgentForge.Domain.Orchestration;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Skills;

namespace AgentForge.Host.Http;

internal sealed record CreateMvpRunRequest(Guid AgentId, string Name);

internal static class ReadyAdminEndpoints
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
        group.MapGet("/runs", ListRunsAsync);
        group.MapPost("/runs", CreateRunAsync);
        group.MapPost("/runs/{taskId:guid}/cancel", CancelRunAsync);
        group.MapGet("/skills", ListSkillsAsync);
        group.MapPost("/skills/seed/csharp-review/install", InstallSeedSkillAsync);
    }

    private static async Task<IResult> CreateSessionAsync(
        HttpContext context,
        ReadyAdminSessionManager sessions,
        IInstallationStateReader stateReader,
        ILocalAdministratorRepository administrators,
        ISecretStore secretStore,
        ILocalAdministratorAuthenticator authenticator,
        IClock clock,
        CancellationToken cancellationToken)
    {
        if (!IsTrustedLoopback(context))
        {
            return Problem(context, 403, "Loopback required",
                "The local operator session can be created only from this loopback origin.", "loopback-required");
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
        var stableRequestIdentity = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{acquired.Session.InstallationId.Value:D}\n{idempotencyKey}"));
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
            idempotencyKey,
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

    private static async Task<IResult> CancelRunAsync(
        Guid taskId,
        HttpContext context,
        ReadyAdminSessionManager sessions,
        IInstallationStateReader stateReader,
        ITaskSnapshotStore snapshots,
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

        var current = await snapshots.FindLatestAsync(new OrchestrationTaskId(taskId), cancellationToken);
        if (current is null || current.Definition.InstallationId != acquired.Session!.InstallationId)
        {
            return Problem(context, 404, "Run not found",
                "No run exists under this installation.", "not-found");
        }
        if (current.State is OrchestrationTaskState.Canceled)
        {
            context.Response.Headers["Idempotent-Replay"] = "true";
            return Results.Ok(RunResponse(current, true));
        }

        var result = await orchestrator.CancelAsync(
            current.Definition.Id, current.Version, cancellationToken);
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
        if (!IsTrustedLoopback(context))
        {
            return new(null, Problem(context, 403, "Loopback required",
                "The operator workspace is available only from this loopback origin.", "loopback-required"));
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

    private static string SnapshotHash<T>(T value) =>
        $"sha256:{Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, HashJson)))}";

    private static string SeedSkillDirectory() =>
        Path.Combine(AppContext.BaseDirectory, "skills", "seed", "csharp-review");

    private static bool Text(string? value, int maximum) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximum && !value.Any(char.IsControl);

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

    private sealed record ReadyAdminAcquisition(ReadyAdminSession? Session, IResult? Failure);
}
