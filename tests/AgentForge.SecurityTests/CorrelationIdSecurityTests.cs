using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AgentForge.SecurityTests;

public sealed class CorrelationIdSecurityTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task Invalid_correlation_identifier_is_rejected_and_not_reflected()
    {
        using var client = factory.CreateClient();
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
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/v1/runtime/ping", CancellationToken.None);
        var body = await response.Content.ReadAsStringAsync(CancellationToken.None);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("setup-required", body, StringComparison.Ordinal);
    }
}
