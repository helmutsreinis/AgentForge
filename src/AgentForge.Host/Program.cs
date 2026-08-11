using AgentForge.Abstractions.Installations;
using AgentForge.Abstractions.Security;
using AgentForge.Abstractions.Tools;
using AgentForge.Abstractions.Tracing;
using AgentForge.Audit;
using AgentForge.Coding;
using AgentForge.Domain.Installations;
using AgentForge.Environment;
using AgentForge.Host.Health;
using AgentForge.Host.Http;
using AgentForge.Memory;
using AgentForge.Models;
using AgentForge.Orchestration;
using AgentForge.Persistence;
using AgentForge.Runtime;
using AgentForge.Search;
using AgentForge.Security;
using AgentForge.Setup;
using AgentForge.Skills;
using AgentForge.Tools;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options => options.IncludeScopes = true);

var configuredUrls = builder.Configuration["AgentForge:Host:Urls"];
builder.WebHost.UseUrls(string.IsNullOrWhiteSpace(configuredUrls)
    ? "http://127.0.0.1:5047"
    : configuredUrls);

builder.Services.AddProblemDetails();
builder.Services.AddAgentForgeSetup(builder.Configuration);
builder.Services.AddAgentForgePersistence(builder.Configuration);
builder.Services.AddAgentForgeSecurity(builder.Configuration);
builder.Services.AddAgentForgeSkills();
builder.Services.AddAgentForgeAudit();
builder.Services.AddAgentForgeCoding();
builder.Services.AddAgentForgeEnvironment(builder.Configuration);
builder.Services.AddAgentForgeTools(builder.Configuration);
builder.Services.AddAgentForgeModels();
builder.Services.AddAgentForgeMemory();
builder.Services.AddAgentForgeOrchestration();
builder.Services.AddAgentForgeRuntime();
builder.Services.AddAgentForgeSearch();
builder.Services.AddSingleton<CorrelationContext>();
builder.Services.AddSingleton<ICorrelationContext>(services => services.GetRequiredService<CorrelationContext>());
builder.Services.AddHealthChecks()
    .AddCheck<InstallationReadinessHealthCheck>("installation", tags: ["ready"]);

var app = builder.Build();

await using (var initializationScope = app.Services.CreateAsyncScope())
{
    var databaseInitializer = initializationScope.ServiceProvider.GetRequiredService<IDatabaseInitializer>();
    await databaseInitializer.InitializeAsync(CancellationToken.None);
}

app.UseExceptionHandler();
app.UseMiddleware<CorrelationIdMiddleware>();
app.Use(async (context, next) =>
{
    context.Response.Headers.ContentSecurityPolicy =
        "default-src 'self'; base-uri 'none'; connect-src 'self'; font-src 'self'; " +
        "form-action 'none'; frame-ancestors 'none'; img-src 'self' data:; object-src 'none'; " +
        "script-src 'self'; style-src 'self'";
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers.XFrameOptions = "DENY";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["Permissions-Policy"] =
        "camera=(), geolocation=(), microphone=(), payment=(), usb=()";
    await next(context);
});
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = context =>
    {
        context.Context.Response.Headers.CacheControl = "no-store";
    },
});

app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = _ => false,
    ResponseWriter = HealthResponseWriter.WriteAsync,
});

app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains("ready"),
    ResponseWriter = HealthResponseWriter.WriteAsync,
});

app.MapGet("/api/v1/setup/status", async (
    IInstallationStateReader stateReader,
    ICorrelationContext correlation,
    CancellationToken cancellationToken) =>
{
    var state = await stateReader.ReadAsync(cancellationToken);
    return Results.Ok(StatusResponse.From(state, correlation.CorrelationId));
});

app.MapGet("/api/v1/status", async (
    IInstallationStateReader stateReader,
    ICorrelationContext correlation,
    CancellationToken cancellationToken) =>
{
    var state = await stateReader.ReadAsync(cancellationToken);
    var response = StatusResponse.From(state, correlation.CorrelationId);
    return Results.Json(response, statusCode: state.IsReady ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
});

app.MapGet("/api/v1/sandbox/capabilities", (
    ISandbox sandbox,
    ICorrelationContext correlation) => Results.Ok(new
    {
        kind = sandbox.Capabilities.Kind.ToString(),
        sandbox.Capabilities.IsAvailable,
        supportedFeatures = sandbox.Capabilities.SupportedFeatures.ToString(),
        sandbox.Capabilities.Evidence,
        correlationId = correlation.CorrelationId,
    }));

app.MapGet("/api/v1/runtime/ping", async (
    HttpRequest request,
    IInstallationStateReader stateReader,
    ILocalAdministratorAuthenticator authenticator,
    ICorrelationContext correlation,
    CancellationToken cancellationToken) =>
{
    var state = await stateReader.ReadAsync(cancellationToken);
    if (!state.IsReady)
    {
        return Results.Problem(
            type: "urn:agentforge:problem:setup-required",
            title: "Setup required",
            detail: "Normal runtime operations are unavailable until installation setup and validation complete.",
            statusCode: StatusCodes.Status503ServiceUnavailable,
            extensions: new Dictionary<string, object?>
            {
                ["correlationId"] = correlation.CorrelationId,
                ["installationState"] = state.State.ToString(),
            });
    }

    if (!TryGetBearerCredential(request, out var credential))
    {
        return Results.Problem(
            type: "urn:agentforge:problem:authentication-required",
            title: "Authentication required",
            detail: "A valid local administrator bearer credential is required.",
            statusCode: StatusCodes.Status401Unauthorized,
            extensions: new Dictionary<string, object?>
            {
                ["correlationId"] = correlation.CorrelationId,
            });
    }

    try
    {
        var authentication = await authenticator.AuthenticateAsync(state.Id, credential, cancellationToken);
        if (!authentication.IsSuccess)
        {
            return Results.Problem(
                type: "urn:agentforge:problem:authentication-failed",
                title: "Authentication failed",
                detail: "The supplied local administrator credential is invalid.",
                statusCode: StatusCodes.Status401Unauthorized,
                extensions: new Dictionary<string, object?>
                {
                    ["correlationId"] = correlation.CorrelationId,
                });
        }

        return Results.Ok(new
        {
            status = "ok",
            actorId = authentication.Value.Value,
            correlationId = correlation.CorrelationId,
        });
    }
    finally
    {
        Array.Clear(credential);
    }
});

static bool TryGetBearerCredential(HttpRequest request, out char[] credential)
{
    credential = [];
    var header = request.Headers.Authorization.ToString();
    const string prefix = "Bearer ";
    if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
    {
        return false;
    }

    var value = header.AsSpan(prefix.Length);
    if (value.Length is < 1 or > 256 || value.Contains(',') || value.Contains('\r') || value.Contains('\n'))
    {
        return false;
    }

    credential = value.ToArray();
    return true;
}

await app.RunAsync();

public partial class Program;
