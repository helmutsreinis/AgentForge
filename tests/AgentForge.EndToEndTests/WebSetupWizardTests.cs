using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentForge.Abstractions.Agents;
using AgentForge.Abstractions.Models;
using AgentForge.Abstractions.Providers;
using AgentForge.Abstractions.Security;
using AgentForge.Domain.Models;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Providers;
using AgentForge.Domain.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentForge.EndToEndTests;

public sealed class WebSetupWizardTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"agentforge-web-setup-{Guid.NewGuid():N}");
    private readonly WebApplicationFactory<Program> _factory;

    public WebSetupWizardTests()
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
                services.AddSingleton<ISecretStore, WebFakeSecretStore>();
                services.RemoveAll<IModelCatalogDiscoveryService>();
                services.AddSingleton<IModelCatalogDiscoveryService, WebFakeModelDiscovery>();
            });
        });
    }

    [Fact]
    public async Task Loopback_wizard_hides_bootstrap_security_discovers_models_resumes_and_completes_shared_setup()
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("http://localhost"),
            HandleCookies = true,
        });
        using var nonceResponse = await client.GetAsync("/api/v1/setup/web/nonce");
        Assert.Equal(HttpStatusCode.NotFound, nonceResponse.StatusCode);
        using var sessionResponse = await client.PostAsJsonAsync("/api/v1/setup/web/session", new { });
        Assert.Equal(HttpStatusCode.OK, sessionResponse.StatusCode);
        var csrf = await ReadPropertyAsync(sessionResponse, "csrfToken");
        Assert.Equal("1", await ReadRawPropertyAsync(sessionResponse, "currentStep"));

        using var missingCsrf = await client.PostAsync("/api/v1/setup/web/begin", JsonContent.Create(new { }));
        Assert.Equal(HttpStatusCode.Unauthorized, missingCsrf.StatusCode);
        var concurrentBegins = await Task.WhenAll(
            MutationAsync(client, "/api/v1/setup/web/begin", "begin-1", csrf, JsonContent.Create(new { })),
            MutationAsync(client, "/api/v1/setup/web/begin", "begin-1", csrf, JsonContent.Create(new { })));
        using var begin = concurrentBegins[0];
        using var beginReplay = concurrentBegins[1];
        Assert.Equal(HttpStatusCode.OK, begin.StatusCode);
        Assert.Equal(HttpStatusCode.OK, beginReplay.StatusCode);
        Assert.Equal(await begin.Content.ReadAsStringAsync(), await beginReplay.Content.ReadAsStringAsync());

        using var discovery = await MutationAsync(client, "/api/v1/setup/web/provider/discover", "provider-discover-1", csrf, JsonContent.Create(new
        {
            name = "primary",
            providerType = "openai-compatible",
            endpoint = "http://192.168.1.89:8000/v1",
        }));
        Assert.Equal(HttpStatusCode.OK, discovery.StatusCode);
        using var models = await MutationAsync(client, "/api/v1/setup/web/provider/models", "models-1", csrf,
            new StringContent(string.Empty, System.Text.Encoding.UTF8, "text/plain"));
        Assert.Equal(HttpStatusCode.OK, models.StatusCode);
        Assert.Contains("qwen3.6", await models.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using var resumed = await client.GetAsync("/api/v1/setup/web/session");
        Assert.Equal(HttpStatusCode.OK, resumed.StatusCode);
        Assert.Equal("true", await ReadRawPropertyAsync(resumed, "resumed"));
        Assert.Equal("2", await ReadRawPropertyAsync(resumed, "currentStep"));

        using var selected = await MutationAsync(client, "/api/v1/setup/web/provider/select", "model-select-1", csrf, JsonContent.Create(new
        {
            name = "primary",
            providerType = "openai-compatible",
            endpoint = "http://192.168.1.89:8000/v1",
            model = "qwen3.6",
        }));
        Assert.Equal(HttpStatusCode.OK, selected.StatusCode);
        using var tested = await MutationAsync(client, "/api/v1/setup/web/provider/test", "model-test-1", csrf,
            new StringContent(string.Empty, System.Text.Encoding.UTF8, "text/plain"));
        Assert.Equal(HttpStatusCode.OK, tested.StatusCode);
        using var credential = await MutationAsync(client, "/api/v1/setup/web/provider/credential", "credential-1", csrf,
            new StringContent(string.Empty, System.Text.Encoding.UTF8, "text/plain"));
        Assert.Equal(HttpStatusCode.OK, credential.StatusCode);

        var agent = new
        {
            name = "web-agent",
            expertise = "safe automation",
            mission = "test shared setup",
            preferredLanguage = "en",
            timeZone = "UTC",
            responseStyle = "concise",
            defaultWorkspace = (string?)null,
        };
        using var preview = await MutationAsync(client, "/api/v1/setup/web/agent/preview", "preview-1", csrf, JsonContent.Create(agent));
        Assert.True(preview.StatusCode == HttpStatusCode.OK, await preview.Content.ReadAsStringAsync());
        using var create = await MutationAsync(client, "/api/v1/setup/web/agent", "agent-1", csrf, JsonContent.Create(agent));
        Assert.True(create.StatusCode == HttpStatusCode.OK, await create.Content.ReadAsStringAsync());
        using var complete = await MutationAsync(client, "/api/v1/setup/web/complete", "complete-1", csrf, JsonContent.Create(new { }));
        Assert.True(complete.StatusCode == HttpStatusCode.OK, await complete.Content.ReadAsStringAsync());
        Assert.Equal("Ready", await ReadPropertyAsync(complete, "state"));
        using var completeDocument = JsonDocument.Parse(await complete.Content.ReadAsByteArrayAsync());
        var installationId = new InstallationId(completeDocument.RootElement.GetProperty("installationId").GetGuid());
        using var completeReplay = await MutationAsync(client, "/api/v1/setup/web/complete", "complete-1", csrf, JsonContent.Create(new { }));
        Assert.Equal(HttpStatusCode.OK, completeReplay.StatusCode);

        using var completedSession = await MutationAsync(client, "/api/v1/setup/web/begin", "after-complete", csrf, JsonContent.Create(new { }));
        Assert.Equal(HttpStatusCode.Conflict, completedSession.StatusCode);
        using var completedSessionRead = await client.GetAsync("/api/v1/setup/web/session");
        Assert.Equal(HttpStatusCode.Conflict, completedSessionRead.StatusCode);
        using var completedSessionCreate = await client.PostAsJsonAsync("/api/v1/setup/web/session", new { });
        Assert.Equal(HttpStatusCode.Conflict, completedSessionCreate.StatusCode);
        using var status = await client.GetAsync("/api/v1/setup/status");
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        Assert.Contains("\"ready\":true", await status.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        await using var verificationScope = _factory.Services.CreateAsyncScope();
        var configuredAgent = Assert.Single(await verificationScope.ServiceProvider
            .GetRequiredService<IAgentIdentityRepository>().ListAsync(installationId, CancellationToken.None));
        Assert.Equal(AgentForge.Domain.Agents.AgentMemoryScope.Agent, configuredAgent.MemoryPolicy.Scope);
        Assert.Equal(30, configuredAgent.MemoryPolicy.RetentionDays);
        Assert.Equal(64, configuredAgent.Budget.MaxTurns);
        Assert.Equal(0, configuredAgent.Budget.MaxToolInvocations);
        Assert.Equal(0, configuredAgent.ChildLimits.MaxChildren);
        Assert.Equal(AgentForge.Domain.Agents.NetworkPosture.Denied, configuredAgent.CapabilityPolicy.NetworkPosture);
        Assert.Equal(AgentForge.Domain.Agents.LearningMode.Propose, configuredAgent.LearningPolicy.Mode);
        var provider = Assert.Single(await verificationScope.ServiceProvider
            .GetRequiredService<IProviderProfileRepository>().ListAsync(installationId, CancellationToken.None));
        Assert.Equal("qwen3.6", provider.Model);
        Assert.True(provider.SecretReference.IsNoCredential);
    }

    [Fact]
    public async Task Loopback_wizard_accepts_an_explicit_manual_model_but_still_requires_selection_and_probe()
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("http://localhost"),
            HandleCookies = true,
        });
        using var sessionResponse = await client.PostAsJsonAsync("/api/v1/setup/web/session", new { });
        Assert.Equal(HttpStatusCode.OK, sessionResponse.StatusCode);
        var csrf = await ReadPropertyAsync(sessionResponse, "csrfToken");
        var provider = new
        {
            name = "primary",
            providerType = "openai-compatible",
            endpoint = "http://127.0.0.1:8000/v1",
        };
        using var discovery = await MutationAsync(client, "/api/v1/setup/web/provider/discover", "manual-discover", csrf, JsonContent.Create(provider));
        Assert.Equal(HttpStatusCode.OK, discovery.StatusCode);
        using var manual = await MutationAsync(client, "/api/v1/setup/web/provider/model/manual", "manual-model", csrf, JsonContent.Create(new
        {
            provider.name,
            provider.providerType,
            provider.endpoint,
            model = "qwen3.6-manual",
        }));
        Assert.Equal(HttpStatusCode.OK, manual.StatusCode);
        Assert.Contains("manual-entry", await manual.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        using var resumed = await client.GetAsync("/api/v1/setup/web/session");
        Assert.Equal("2", await ReadRawPropertyAsync(resumed, "currentStep"));
        using var rejectedProbe = await MutationAsync(client, "/api/v1/setup/web/provider/test", "manual-probe-before-select", csrf,
            new StringContent(string.Empty, System.Text.Encoding.UTF8, "text/plain"));
        Assert.Equal(HttpStatusCode.BadRequest, rejectedProbe.StatusCode);
        using var selected = await MutationAsync(client, "/api/v1/setup/web/provider/select", "manual-select", csrf, JsonContent.Create(new
        {
            provider.name,
            provider.providerType,
            provider.endpoint,
            model = "qwen3.6-manual",
        }));
        Assert.Equal(HttpStatusCode.OK, selected.StatusCode);
        using var tested = await MutationAsync(client, "/api/v1/setup/web/provider/test", "manual-probe", csrf,
            new StringContent(string.Empty, System.Text.Encoding.UTF8, "text/plain"));
        Assert.Equal(HttpStatusCode.OK, tested.StatusCode);
    }

    private static async Task<HttpResponseMessage> MutationAsync(
        HttpClient client, string path, string key, string csrf, HttpContent content)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = content };
        request.Headers.Add("Idempotency-Key", key);
        request.Headers.Add("X-CSRF-Token", csrf);
        request.Headers.Add("Origin", "http://localhost");
        return await client.SendAsync(request);
    }

    private static async Task<string> ReadPropertyAsync(HttpResponseMessage response, string property)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
        return document.RootElement.GetProperty(property).GetString()!;
    }

    private static async Task<string> ReadRawPropertyAsync(HttpResponseMessage response, string property)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
        return document.RootElement.GetProperty(property).GetRawText();
    }

    public void Dispose()
    {
        _factory.Dispose();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    private sealed class WebFakeSecretStore : ISecretStore
    {
        private readonly ConcurrentDictionary<string, char[]> _values = new(StringComparer.Ordinal);
        public string StoreName => "web-fake";
        public SecretStoreCapability GetCapability() => new(StoreName, true, null);
        public Task<DomainResult<SecretReference>> StoreAsync(
            string logicalName, ReadOnlyMemory<char> secret, CancellationToken cancellationToken)
        {
            var key = $"secret-{Guid.NewGuid():N}";
            _values[key] = secret.ToArray();
            return Task.FromResult(DomainResult.Success(new SecretReference(StoreName, key)));
        }
        public Task<DomainResult<SecretLease>> MaterializeAsync(SecretReference secretReference, CancellationToken cancellationToken) =>
            Task.FromResult(_values.TryGetValue(secretReference.Key, out var value)
                ? DomainResult.Success(new SecretLease(value.ToArray()))
                : DomainResult.Fail<SecretLease>(new DomainFailure(FailureCode.UnsupportedCapability, "Secret unavailable.")));
        public Task<DomainResult<bool>> DeleteAsync(SecretReference secretReference, CancellationToken cancellationToken) =>
            Task.FromResult(DomainResult.Success(_values.TryRemove(secretReference.Key, out _)));
    }

    private sealed class WebFakeModelDiscovery : IModelCatalogDiscoveryService
    {
        public Task<DomainResult<ModelCatalogDiscoveryResult>> DiscoverAsync(
            ModelCatalogDiscoveryRequest request,
            CancellationToken cancellationToken) => Task.FromResult(DomainResult.Success(new ModelCatalogDiscoveryResult(
                [new ModelCatalogEntry("qwen3.6", "vllm", 131_072)],
                new Uri(request.BaseEndpoint, request.BaseEndpoint.AbsolutePath.TrimEnd('/') + "/models"),
                DateTimeOffset.UnixEpoch)));

        public Task<DomainResult<ModelConnectionProbeResult>> ProbeAsync(
            ModelConnectionProbeRequest request,
            CancellationToken cancellationToken) => Task.FromResult(DomainResult.Success(new ModelConnectionProbeResult(
                request.Model,
                new Uri(request.BaseEndpoint, request.BaseEndpoint.AbsolutePath.TrimEnd('/') + "/chat/completions"),
                TimeSpan.FromMilliseconds(12),
                "web-fake-probe")));
    }
}
