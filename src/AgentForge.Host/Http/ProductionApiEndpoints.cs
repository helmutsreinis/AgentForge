using System.Globalization;
using System.Text.Json;
using AgentForge.Abstractions.Installations;
using AgentForge.Abstractions.Orchestration;
using AgentForge.Abstractions.Security;
using AgentForge.Domain.Agents;
using AgentForge.Domain.Orchestration;
using AgentForge.Domain.Primitives;

namespace AgentForge.Host.Http;

internal sealed record CreateTaskApiRequest(
    Guid TaskId,
    Guid InstallationId,
    Guid AgentId,
    long AgentVersion,
    string Pattern,
    IReadOnlyList<TaskNodeApiRequest> Nodes,
    int MaximumConcurrency,
    int MaximumDelegationDepth,
    int MaximumChildren,
    string PolicySnapshotHash,
    string BudgetSnapshotHash,
    string SkillSnapshotHash);

internal sealed record TaskNodeApiRequest(
    string Id,
    string Name,
    IReadOnlyList<string> Dependencies,
    IReadOnlyList<string> RequiredCapabilities,
    IReadOnlyList<string> ContextEvidenceHashes,
    int MaximumToolCalls,
    long MaximumInputTokens,
    long MaximumOutputTokens,
    int MaximumWallClockSeconds,
    int MaximumAttempts,
    int RetryDelaySeconds,
    string? CompensationNodeId);

internal static class ProductionApiEndpoints
{
    private static readonly JsonSerializerOptions EventJson = new(JsonSerializerDefaults.Web);

    public static void MapProductionApi(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapGet("/api/v1/openapi.json", () => Results.Json(
            OpenApiDocumentFactory.Create(),
            EventJson,
            contentType: "application/vnd.oai.openapi+json;version=3.1"))
            .RequireRateLimiting("production-api");

        app.MapPost("/api/v1/tasks", CreateTaskAsync).RequireRateLimiting("production-api");
        app.MapGet("/api/v1/tasks/{taskId:guid}", GetTaskAsync).RequireRateLimiting("production-api");
        app.MapGet("/api/v1/tasks/{taskId:guid}/events", StreamTaskAsync)
            .RequireRateLimiting("production-api");
    }

    private static async Task<IResult> CreateTaskAsync(
        HttpContext context,
        CreateTaskApiRequest request,
        IInstallationStateReader stateReader,
        ILocalAdministratorAuthenticator authenticator,
        ITaskOrchestrator orchestrator,
        CancellationToken cancellationToken)
    {
        var auth = await ApiAuthentication.AuthenticateAsync(
            context, stateReader, authenticator, cancellationToken);
        if (!auth.Succeeded) return auth.Failure!;
        var key = context.Request.Headers["Idempotency-Key"].ToString();
        if (!IsIdentifier(key, 256))
            return Problem(context, StatusCodes.Status400BadRequest, "Idempotency required",
                "A bounded Idempotency-Key is required for every mutation.", "idempotency-required");
        if (request.InstallationId != auth.Installation!.Id.Value)
            return Problem(context, StatusCodes.Status403Forbidden, "Installation denied",
                "The request installation does not match authenticated authority.", "scope-denied");
        if (!Enum.TryParse<OrchestrationPattern>(request.Pattern, ignoreCase: false, out var pattern) ||
            request.Nodes is null || request.Nodes.Count is < 1 or > 256)
            return Problem(context, StatusCodes.Status400BadRequest, "Invalid task",
                "Task pattern and node collection are invalid.", "validation-failure");

        OrchestrationTaskDefinition definition;
        try
        {
            definition = new OrchestrationTaskDefinition(
                new OrchestrationTaskId(request.TaskId),
                new InstallationId(request.InstallationId),
                new AgentIdentityId(request.AgentId),
                request.AgentVersion,
                pattern,
                request.Nodes.Select(MapNode).ToArray(),
                request.MaximumConcurrency,
                request.MaximumDelegationDepth,
                request.MaximumChildren,
                request.PolicySnapshotHash,
                request.BudgetSnapshotHash,
                request.SkillSnapshotHash);
        }
        catch (Exception exception) when (exception is ArgumentException or OverflowException)
        {
            return Problem(context, StatusCodes.Status400BadRequest, "Invalid task",
                "The task definition could not be normalized.", "validation-failure");
        }

        var created = await orchestrator.CreateAsync(
            definition,
            auth.Actor!.Value,
            key,
            new CorrelationId(context.TraceIdentifier),
            null,
            cancellationToken);
        if (!created.IsSuccess) return DomainProblem(context, created.Failure!);
        if (created.Value.WasReplay) context.Response.Headers["Idempotent-Replay"] = "true";
        var response = TaskResponse(created.Value.Snapshot, created.Value.WasReplay);
        return Results.Created($"/api/v1/tasks/{request.TaskId:D}", response);
    }

    private static async Task<IResult> GetTaskAsync(
        Guid taskId,
        HttpContext context,
        IInstallationStateReader stateReader,
        ILocalAdministratorAuthenticator authenticator,
        ITaskSnapshotStore snapshots,
        CancellationToken cancellationToken)
    {
        var auth = await ApiAuthentication.AuthenticateAsync(
            context, stateReader, authenticator, cancellationToken);
        if (!auth.Succeeded) return auth.Failure!;
        var snapshot = await snapshots.FindLatestAsync(new OrchestrationTaskId(taskId), cancellationToken);
        return snapshot is null || snapshot.Definition.InstallationId != auth.Installation!.Id
            ? Problem(context, StatusCodes.Status404NotFound, "Task not found",
                "No task exists under the authenticated installation.", "not-found")
            : Results.Ok(TaskResponse(snapshot, false));
    }

    private static async Task StreamTaskAsync(
        Guid taskId,
        long? afterVersion,
        bool? follow,
        HttpContext context,
        IInstallationStateReader stateReader,
        ILocalAdministratorAuthenticator authenticator,
        ITaskSnapshotStore snapshots,
        IEventStream events,
        CancellationToken cancellationToken)
    {
        var auth = await ApiAuthentication.AuthenticateAsync(
            context, stateReader, authenticator, cancellationToken);
        if (!auth.Succeeded)
        {
            await auth.Failure!.ExecuteAsync(context);
            return;
        }
        var id = new OrchestrationTaskId(taskId);
        var current = await snapshots.FindLatestAsync(id, cancellationToken);
        if (current is null || current.Definition.InstallationId != auth.Installation!.Id)
        {
            await Problem(context, StatusCodes.Status404NotFound, "Task not found",
                "No task exists under the authenticated installation.", "not-found").ExecuteAsync(context);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["X-Accel-Buffering"] = "no";
        await foreach (var item in events.ReadTaskAsync(
            id, afterVersion ?? -1, follow ?? false, cancellationToken))
        {
            await context.Response.WriteAsync(
                $"id: {item.Version.ToString(CultureInfo.InvariantCulture)}\n", cancellationToken);
            await context.Response.WriteAsync("event: task-progress\n", cancellationToken);
            await context.Response.WriteAsync(
                $"data: {JsonSerializer.Serialize(item, EventJson)}\n\n", cancellationToken);
            await context.Response.Body.FlushAsync(cancellationToken);
        }
    }

    private static TaskNodeDefinition MapNode(TaskNodeApiRequest node) => new(
        new TaskNodeId(node.Id),
        node.Name,
        (node.Dependencies ?? []).Select(value => new TaskNodeId(value)).ToArray(),
        node.RequiredCapabilities ?? [],
        node.ContextEvidenceHashes ?? [],
        new TaskExecutionBudget(
            node.MaximumToolCalls,
            node.MaximumInputTokens,
            node.MaximumOutputTokens,
            node.MaximumWallClockSeconds),
        new TaskRetryPolicy(node.MaximumAttempts, node.RetryDelaySeconds),
        string.IsNullOrWhiteSpace(node.CompensationNodeId)
            ? null
            : new TaskNodeId(node.CompensationNodeId));

    private static object TaskResponse(OrchestrationTaskSnapshot snapshot, bool replay) => new
    {
        taskId = snapshot.Definition.Id.Value,
        installationId = snapshot.Definition.InstallationId.Value,
        snapshot.Version,
        state = snapshot.State.ToString(),
        snapshot.SnapshotHash,
        snapshot.UpdatedAt,
        wasReplay = replay,
        correlationId = snapshot.CorrelationId.Value,
    };

    private static IResult DomainProblem(HttpContext context, DomainFailure failure)
    {
        var status = failure.Code switch
        {
            FailureCode.ConcurrencyConflict => StatusCodes.Status409Conflict,
            FailureCode.ApprovalRequired => StatusCodes.Status403Forbidden,
            FailureCode.PolicyDenied => StatusCodes.Status403Forbidden,
            FailureCode.UnsupportedCapability => StatusCodes.Status422UnprocessableEntity,
            FailureCode.BudgetExceeded => StatusCodes.Status422UnprocessableEntity,
            FailureCode.RecoverableExternalFailure => StatusCodes.Status503ServiceUnavailable,
            _ => StatusCodes.Status400BadRequest,
        };
        return Problem(context, status, "Task request failed", failure.Message,
            failure.Code.ToString().ToLowerInvariant());
    }

    private static IResult Problem(
        HttpContext context, int status, string title, string detail, string code) => Results.Problem(
        type: $"urn:agentforge:problem:{code}",
        title: title,
        detail: detail,
        statusCode: status,
        extensions: new Dictionary<string, object?> { ["correlationId"] = context.TraceIdentifier });

    private static bool IsIdentifier(string value, int maximum) => value.Length is > 0 && value.Length <= maximum &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-' or ':');
}
