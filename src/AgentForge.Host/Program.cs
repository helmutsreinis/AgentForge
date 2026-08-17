using System.Threading.RateLimiting;
using AgentForge.Abstractions.Installations;
using AgentForge.Abstractions.Security;
using AgentForge.Abstractions.Tools;
using AgentForge.Abstractions.Tracing;
using AgentForge.Audit;
using AgentForge.Channels;
using AgentForge.Coding;
using AgentForge.Devices;
using AgentForge.Domain.Installations;
using AgentForge.Environment;
using AgentForge.Host.Health;
using AgentForge.Host.Http;
using AgentForge.Host.Setup;
using AgentForge.HttpApi;
using AgentForge.Learning;
using AgentForge.Mcp;
using AgentForge.Memory;
using AgentForge.Models;
using AgentForge.Orchestration;
using AgentForge.Persistence;
using AgentForge.Plugins;
using AgentForge.Runtime;
using AgentForge.Search;
using AgentForge.Security;
using AgentForge.Setup;
using AgentForge.Skills;
using AgentForge.Tools;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseWindowsService(options => options.ServiceName = "AgentForge");
builder.Host.UseSystemd();

builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options => options.IncludeScopes = true);

var configuredUrls = builder.Configuration["AgentForge:Host:Urls"];
builder.WebHost.UseUrls(string.IsNullOrWhiteSpace(configuredUrls)
    ? "http://127.0.0.1:5047"
    : configuredUrls);

builder.Services.AddProblemDetails();
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor |
        ForwardedHeaders.XForwardedHost | ForwardedHeaders.XForwardedProto;
    options.ForwardLimit = 1;
    options.KnownProxies.Clear();
    options.KnownProxies.Add(System.Net.IPAddress.Loopback);
    options.KnownProxies.Add(System.Net.IPAddress.IPv6Loopback);
});
builder.Services.AddSingleton<IValidateOptions<HostSecurityOptions>, HostSecurityOptionsValidator>();
builder.Services.AddOptions<HostSecurityOptions>()
    .Bind(builder.Configuration.GetSection(HostSecurityOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddRateLimiter(options => options.AddPolicy("production-api", context =>
    RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "local",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = builder.Configuration.GetValue(
                "AgentForge:Host:RequestsPerMinute", 120),
            QueueLimit = 0,
            Window = TimeSpan.FromMinutes(1),
            AutoReplenishment = true,
        })));
var allowedOrigins = builder.Configuration.GetSection("AgentForge:Host:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(options => options.AddPolicy("mcp-browser", policy =>
{
    if (allowedOrigins.Length > 0) policy.WithOrigins(allowedOrigins);
    else policy.SetIsOriginAllowed(_ => false);
    policy.WithMethods("POST", "OPTIONS")
        .WithHeaders("Content-Type", "Authorization", "MCP-Protocol-Version");
}));
builder.Services.AddAgentForgeSetup(builder.Configuration);
builder.Services.AddAgentForgePersistence(builder.Configuration);
builder.Services.AddAgentForgePlugins(builder.Configuration);
builder.Services.AddAgentForgeSecurity(builder.Configuration);
builder.Services.AddAgentForgeSkills();
builder.Services.AddAgentForgeLearning();
builder.Services.AddAgentForgeAudit();
builder.Services.AddAgentForgeCoding();
builder.Services.AddAgentForgeChannels();
builder.Services.AddAgentForgeDevices();
builder.Services.AddAgentForgeEnvironment(builder.Configuration);
builder.Services.AddAgentForgeHttpApi();
builder.Services.AddAgentForgeTools(builder.Configuration);
builder.Services.AddAgentForgeModels();
builder.Services.AddAgentForgeMemory();
builder.Services.AddAgentForgeMcp(builder.Configuration)
    .WithHttpTransport(options => options.Stateless = true);
builder.Services.AddAgentForgeOrchestration();
builder.Services.AddAgentForgeRuntime();
builder.Services.AddAgentForgeSearch();
builder.Services.AddSingleton<CorrelationContext>();
builder.Services.AddSingleton<ICorrelationContext>(services => services.GetRequiredService<CorrelationContext>());
builder.Services.AddSingleton<WebSetupSessionManager>();
builder.Services.AddSingleton<ReadyAdminSessionManager>();
builder.Services.AddSingleton<ReadyActiveInteractionRegistry>();
builder.Services.AddHealthChecks()
    .AddCheck<InstallationReadinessHealthCheck>("installation", tags: ["ready"]);

var app = builder.Build();

await using (var initializationScope = app.Services.CreateAsyncScope())
{
    var databaseInitializer = initializationScope.ServiceProvider.GetRequiredService<IDatabaseInitializer>();
    await databaseInitializer.InitializeAsync(CancellationToken.None);
}

app.UseForwardedHeaders();
app.UseExceptionHandler();
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<RemoteAccessMiddleware>();
app.UseMiddleware<McpAuthenticationMiddleware>();
app.UseRateLimiter();
app.UseCors();
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api/v1"))
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["AgentForge-Api-Version"] = "1.0";
        context.Response.Headers["api-supported-versions"] = "1.0";
    }
    if (context.Request.Path.StartsWithSegments("/api/v1/setup/web"))
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";
    }
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

    if (!ApiAuthentication.TryGetBearerCredential(request, out var credential))
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

app.MapAgentForgeWebSetup();
app.MapReadyAdminApi();
app.MapProductionApi();
app.MapMcp("/mcp")
    .RequireCors("mcp-browser")
    .RequireRateLimiting("production-api");

await app.RunAsync();

public partial class Program;
