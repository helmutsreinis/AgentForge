using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AgentForge.Abstractions.Security;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace AgentForge.IntegrationTests;

public sealed class ProductionApiTests : IDisposable
{
    private const string HashA = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string HashB = "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string HashC = "sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"agentforge-api-{Guid.NewGuid():N}");
    private readonly WebApplicationFactory<Program> _factory;

    public ProductionApiTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["AgentForge:Installation:DataDirectory"] = _directory,
                    ["AgentForge:Host:Urls"] = string.Empty,
                    ["AgentForge:Persistence:EnableConnectionPooling"] = "false",
                }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ISecretStore>();
                services.AddSingleton<ISecretStore, ApiFakeSecretStore>();
            });
        });
    }

    [Fact]
    public async Task Authenticated_task_api_requires_idempotency_and_streams_redacted_versioned_events()
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("http://localhost"),
            HandleCookies = true,
        });
        using var openApi = await client.GetAsync("/api/v1/openapi.json");
        var openApiText = await openApi.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, openApi.StatusCode);
        Assert.Contains("\"openapi\":\"3.1.0\"", openApiText, StringComparison.Ordinal);
        Assert.DoesNotContain("apiKey", openApiText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("clientCredentialReference", openApiText, StringComparison.OrdinalIgnoreCase);

        var setup = await CompleteSetupAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", setup.Credential);
        var task = new
        {
            taskId = Guid.NewGuid(),
            installationId = setup.InstallationId,
            agentId = setup.AgentId,
            agentVersion = 0,
            pattern = "Sequential",
            nodes = new[]
            {
                new
                {
                    id = "inspect",
                    name = "Inspect bounded evidence",
                    dependencies = Array.Empty<string>(),
                    requiredCapabilities = new[] { "inventory:read" },
                    contextEvidenceHashes = new[] { HashA },
                    maximumToolCalls = 0,
                    maximumInputTokens = 100,
                    maximumOutputTokens = 100,
                    maximumWallClockSeconds = 30,
                    maximumAttempts = 1,
                    retryDelaySeconds = 0,
                    compensationNodeId = (string?)null,
                },
            },
            maximumConcurrency = 1,
            maximumDelegationDepth = 0,
            maximumChildren = 0,
            policySnapshotHash = HashA,
            budgetSnapshotHash = HashB,
            skillSnapshotHash = HashC,
        };

        using var missingKey = await client.PostAsJsonAsync("/api/v1/tasks", task);
        Assert.Equal(HttpStatusCode.BadRequest, missingKey.StatusCode);
        using var created = await MutationAsync(client, "/api/v1/tasks", "task-create-1", task);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("1.0", created.Headers.GetValues("AgentForge-Api-Version").Single());
        Assert.Equal("1.0", created.Headers.GetValues("api-supported-versions").Single());
        Assert.Equal("no-store", created.Headers.CacheControl?.ToString());
        Assert.False(created.Headers.Contains("Idempotent-Replay"));
        using var replay = await MutationAsync(client, "/api/v1/tasks", "task-create-1", task);
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal("true", replay.Headers.GetValues("Idempotent-Replay").Single());

        using var conflicting = await MutationAsync(client, "/api/v1/tasks", "task-create-1",
            new
            {
                task.taskId,
                task.installationId,
                task.agentId,
                agentVersion = 1,
                task.pattern,
                task.nodes,
                task.maximumConcurrency,
                task.maximumDelegationDepth,
                task.maximumChildren,
                task.policySnapshotHash,
                task.budgetSnapshotHash,
                task.skillSnapshotHash
            });
        Assert.Equal(HttpStatusCode.Conflict, conflicting.StatusCode);

        using var get = await client.GetAsync($"/api/v1/tasks/{task.taskId:D}");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Contains("\"state\":\"Planned\"", await get.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        using var stream = await client.GetAsync($"/api/v1/tasks/{task.taskId:D}/events?afterVersion=-1&follow=false");
        var streamText = await stream.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, stream.StatusCode);
        Assert.Equal("text/event-stream", stream.Content.Headers.ContentType?.MediaType);
        Assert.Contains("id: 0", streamText, StringComparison.Ordinal);
        Assert.Contains("event: task-progress", streamText, StringComparison.Ordinal);
        Assert.Contains("\"snapshotHash\":\"sha256:", streamText, StringComparison.Ordinal);
        Assert.DoesNotContain("Inspect bounded evidence", streamText, StringComparison.Ordinal);

        client.DefaultRequestHeaders.Authorization = null;
        using var unauthenticated = await client.GetAsync($"/api/v1/tasks/{task.taskId:D}/events");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);

        using var mcpHttp = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("http://localhost"),
        });
        mcpHttp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", setup.Credential);
        await using var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri("http://localhost/mcp"),
            TransportMode = HttpTransportMode.StreamableHttp,
            EnableStandaloneGetStream = false,
        }, mcpHttp, loggerFactory: null, ownsHttpClient: false);
        await using var mcp = await McpClient.CreateAsync(transport);
        var tools = await mcp.ListToolsAsync();
        Assert.Equal("agentforge_status", Assert.Single(tools).Name);
        var resources = await mcp.ListResourcesAsync();
        Assert.Equal("agentforge://status", Assert.Single(resources).Uri);
        var result = await mcp.CallToolAsync("agentforge_status", new Dictionary<string, object?>());
        var statusText = Assert.Single(result.Content.OfType<TextContentBlock>()).Text;
        Assert.Contains("\"ready\":true", statusText, StringComparison.Ordinal);
    }

    [Fact]
    public void Insecure_remote_binding_fails_startup_validation()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["AgentForge:Installation:DataDirectory"] = Path.Combine(_directory, "remote"),
                    ["AgentForge:Host:Urls"] = "http://0.0.0.0:5047",
                    ["AgentForge:Host:RemoteEnabled"] = "false",
                })));
        var exception = Assert.Throws<OptionsValidationException>(() => factory.CreateClient());
        Assert.Contains("explicit remote mode", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<(Guid InstallationId, Guid AgentId, string Credential)> CompleteSetupAsync(HttpClient client)
    {
        using var nonceResponse = await client.GetAsync("/api/v1/setup/web/nonce");
        using var nonceJson = JsonDocument.Parse(await nonceResponse.Content.ReadAsByteArrayAsync());
        using var session = await client.PostAsJsonAsync("/api/v1/setup/web/session", new
        {
            nonce = nonceJson.RootElement.GetProperty("nonce").GetString(),
        });
        using var sessionJson = JsonDocument.Parse(await session.Content.ReadAsByteArrayAsync());
        var csrf = sessionJson.RootElement.GetProperty("csrfToken").GetString()!;
        using var begin = await WebMutationAsync(client, "/api/v1/setup/web/begin", "begin", csrf, new { });
        Assert.Equal(HttpStatusCode.OK, begin.StatusCode);
        using var provider = await WebMutationAsync(client, "/api/v1/setup/web/provider", "provider", csrf, new
        {
            name = "primary",
            providerType = "deterministic",
            endpoint = "http://127.0.0.1:9000/v1",
            model = "deterministic-text-v1",
        });
        Assert.Equal(HttpStatusCode.OK, provider.StatusCode);
        using var credential = await WebMutationAsync(
            client, "/api/v1/setup/web/provider/credential", "provider-credential", csrf,
            new StringContent("api-provider-credential", System.Text.Encoding.UTF8, "text/plain"));
        Assert.Equal(HttpStatusCode.OK, credential.StatusCode);
        var agentRequest = new
        {
            name = "api-agent",
            expertise = "bounded orchestration",
            mission = "verify the production API",
            preferredLanguage = "en",
            timeZone = "UTC",
            responseStyle = "concise",
            defaultWorkspace = (string?)null,
        };
        using var preview = await WebMutationAsync(
            client, "/api/v1/setup/web/agent/preview", "preview", csrf, agentRequest);
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        using var agent = await WebMutationAsync(
            client, "/api/v1/setup/web/agent", "agent", csrf, agentRequest);
        using var agentJson = JsonDocument.Parse(await agent.Content.ReadAsByteArrayAsync());
        var agentId = agentJson.RootElement.GetProperty("agentId").GetGuid();
        using var complete = await WebMutationAsync(
            client, "/api/v1/setup/web/complete", "complete", csrf, new { });
        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
        using var completeJson = JsonDocument.Parse(await complete.Content.ReadAsByteArrayAsync());
        var installationId = completeJson.RootElement.GetProperty("installationId").GetGuid();
        var referenceJson = completeJson.RootElement.GetProperty("administratorCredentialReference");
        var reference = new SecretReference(
            referenceJson.GetProperty("store").GetString()!, referenceJson.GetProperty("key").GetString()!);
        var materialized = await _factory.Services.GetRequiredService<ISecretStore>()
            .MaterializeAsync(reference, CancellationToken.None);
        Assert.True(materialized.IsSuccess, materialized.Failure?.Message);
        using var lease = materialized.Value;
        return (installationId, agentId, new string(lease.Value.Span));
    }

    private static async Task<HttpResponseMessage> MutationAsync(
        HttpClient client, string path, string key, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        request.Headers.Add("Idempotency-Key", key);
        request.Headers.Add("X-Correlation-Id", $"correlation-{key}");
        return await client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> WebMutationAsync(
        HttpClient client, string path, string key, string csrf, object body) =>
        WebMutationAsync(client, path, key, csrf, JsonContent.Create(body));

    private static async Task<HttpResponseMessage> WebMutationAsync(
        HttpClient client, string path, string key, string csrf, HttpContent body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = body };
        request.Headers.Add("Idempotency-Key", key);
        request.Headers.Add("X-CSRF-Token", csrf);
        request.Headers.Add("Origin", "http://localhost");
        return await client.SendAsync(request);
    }

    private sealed class ApiFakeSecretStore : ISecretStore
    {
        private readonly ConcurrentDictionary<string, char[]> _values = new(StringComparer.Ordinal);
        public string StoreName => "api-fake";
        public SecretStoreCapability GetCapability() => new(StoreName, true, null);
        public Task<DomainResult<SecretReference>> StoreAsync(
            string logicalName, ReadOnlyMemory<char> secret, CancellationToken cancellationToken)
        {
            var key = $"secret-{Guid.NewGuid():N}";
            _values[key] = secret.ToArray();
            return Task.FromResult(DomainResult.Success(new SecretReference(StoreName, key)));
        }
        public Task<DomainResult<SecretLease>> MaterializeAsync(
            SecretReference secretReference, CancellationToken cancellationToken) => Task.FromResult(
                _values.TryGetValue(secretReference.Key, out var value)
                    ? DomainResult.Success(new SecretLease(value.ToArray()))
                    : DomainResult.Fail<SecretLease>(new DomainFailure(
                        FailureCode.UnsupportedCapability, "Secret unavailable.")));
        public Task<DomainResult<bool>> DeleteAsync(
            SecretReference secretReference, CancellationToken cancellationToken) =>
            Task.FromResult(DomainResult.Success(_values.TryRemove(secretReference.Key, out _)));
    }

    public void Dispose()
    {
        _factory.Dispose();
        try
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // TestServer can release the SQLite file asynchronously after startup validation failure.
        }
    }
}
