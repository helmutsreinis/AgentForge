using AgentForge.Abstractions.Installations;
using AgentForge.Abstractions.Tracing;
using AgentForge.Audit;
using AgentForge.Domain.Installations;
using AgentForge.Host.Health;
using AgentForge.Host.Http;
using AgentForge.Persistence;
using AgentForge.Security;
using AgentForge.Setup;
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
builder.Services.AddAgentForgeAudit();
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

app.MapGet("/api/v1/runtime/ping", async (
    IInstallationStateReader stateReader,
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

    return Results.Ok(new
    {
        status = "ok",
        correlationId = correlation.CorrelationId,
    });
});

await app.RunAsync();

public partial class Program;
