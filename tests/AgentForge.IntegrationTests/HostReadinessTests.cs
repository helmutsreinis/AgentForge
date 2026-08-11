using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace AgentForge.IntegrationTests;

public sealed class HostReadinessTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"agentforge-integration-{Guid.NewGuid():N}");
    private readonly WebApplicationFactory<Program> _factory;

    public HostReadinessTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["AgentForge:Installation:DataDirectory"] = _directory,
                    ["AgentForge:Host:Urls"] = string.Empty,
                    ["AgentForge:Persistence:EnableConnectionPooling"] = "false",
                });
            });
        });
    }

    [Fact]
    public async Task Live_is_healthy_but_ready_is_unhealthy_before_setup()
    {
        using var client = _factory.CreateClient();

        using var live = await client.GetAsync("/health/live", CancellationToken.None);
        using var ready = await client.GetAsync("/health/ready", CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
    }

    [Fact]
    public async Task Setup_status_is_available_and_returns_correlation_id()
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/setup/status");
        request.Headers.Add("X-Correlation-Id", "integration-test-1");

        using var response = await client.SendAsync(request, CancellationToken.None);
        var body = await response.Content.ReadAsStringAsync(CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("integration-test-1", response.Headers.GetValues("X-Correlation-Id").Single());
        Assert.Contains("Uninitialized", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Root_serves_loopback_setup_wizard_with_security_headers()
    {
        using var client = _factory.CreateClient();
        using var response = await client.GetAsync("/", CancellationToken.None);
        var body = await response.Content.ReadAsStringAsync(CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("AgentForge · Local control plane", body, StringComparison.Ordinal);
        Assert.Contains("Secure first-run setup", body, StringComparison.Ordinal);
        Assert.Contains("<form", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("type=\"password\"", body, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Contains("frame-ancestors 'none'", response.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);
        Assert.Contains("form-action 'none'", response.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
    }

    [Theory]
    [InlineData("/styles.css", "text/css")]
    [InlineData("/app.js", "text/javascript")]
    public async Task Preview_assets_are_local_and_not_cached(string path, string expectedMediaType)
    {
        using var client = _factory.CreateClient();
        using var response = await client.GetAsync(path, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expectedMediaType, response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
    }

    [Fact]
    public async Task Sandbox_capabilities_report_enforced_and_unavailable_isolation()
    {
        using var client = _factory.CreateClient();
        using var response = await client.GetAsync("/api/v1/sandbox/capabilities", CancellationToken.None);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(CancellationToken.None));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("RestrictedHost", document.RootElement.GetProperty("kind").GetString());
        Assert.True(document.RootElement.GetProperty("isAvailable").GetBoolean());
        var features = document.RootElement.GetProperty("supportedFeatures").GetString();
        Assert.Contains("ArgumentArray", features, StringComparison.Ordinal);
        Assert.DoesNotContain("NetworkIsolation", features, StringComparison.Ordinal);
        Assert.DoesNotContain("FileSystemIsolation", features, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        _factory.Dispose();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
