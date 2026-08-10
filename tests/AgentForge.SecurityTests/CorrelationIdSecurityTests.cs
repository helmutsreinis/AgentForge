using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace AgentForge.SecurityTests;

public sealed class CorrelationIdSecurityTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"agentforge-security-{Guid.NewGuid():N}");
    private readonly WebApplicationFactory<Program> _factory;

    public CorrelationIdSecurityTests()
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
    public async Task Invalid_correlation_identifier_is_rejected_and_not_reflected()
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        request.Headers.TryAddWithoutValidation("X-Correlation-Id", "line-break%0d%0aInjected:true");

        using var response = await client.SendAsync(request, CancellationToken.None);
        var body = await response.Content.ReadAsStringAsync(CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain("Injected:true", response.Headers.ToString(), StringComparison.Ordinal);
        Assert.Contains("invalid-correlation-id", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Runtime_endpoint_fails_closed_before_setup()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync("/api/v1/runtime/ping", CancellationToken.None);
        var body = await response.Content.ReadAsStringAsync(CancellationToken.None);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("setup-required", body, StringComparison.Ordinal);
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
