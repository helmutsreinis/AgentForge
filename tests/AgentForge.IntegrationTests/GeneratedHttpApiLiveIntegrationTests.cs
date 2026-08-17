using System.Text.Json;
using AgentForge.Abstractions.Time;
using AgentForge.Domain.HttpApi;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Security;
using AgentForge.HttpApi;

namespace AgentForge.IntegrationTests;

public sealed class GeneratedHttpApiLiveIntegrationTests
{
    private const string BearerEnvironmentVariable = "AGENTFORGE_LIVE_PARTNER_CENTER_BEARER_TOKEN";

    [PartnerCenterBearerFact]
    public async Task Generic_generated_skill_http_tool_can_discover_partner_center_customers()
    {
        var token = System.Environment.GetEnvironmentVariable(BearerEnvironmentVariable)!;
        var now = DateTimeOffset.UtcNow;
        var profile = new HttpApiProfile(
            new InstallationId(Guid.NewGuid()),
            new HttpApiProfileId("microsoft-partner-center"),
            "Microsoft Partner Center",
            new Uri("https://api.partnercenter.microsoft.com/v1/"),
            "customers?size=1",
            new Dictionary<string, string>
            {
                ["MS-CorrelationId"] = "{correlationId}",
                ["MS-RequestId"] = "{requestId}",
            },
            SecretReference.NoCredential,
            true,
            0,
            now,
            now,
            new ActorId("live-gate"),
            new CorrelationId("live-gate"));
        using var client = HttpApiClient.Create(new SystemClock());

        var result = await client.GetAsync(profile, token.AsMemory(), new HttpApiReadRequest(
            "customers", new Dictionary<string, string> { ["size"] = "1" }, 262_144,
            Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D")), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Failure?.Message);
        using var document = JsonDocument.Parse(result.Value.Body);
        Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
        Assert.Equal("https://api.partnercenter.microsoft.com/v1/customers?size=1",
            result.Value.Endpoint.AbsoluteUri);
        Assert.DoesNotContain(token, result.Value.EvidenceHash, StringComparison.Ordinal);
    }

    private sealed class SystemClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }
}

internal sealed class PartnerCenterBearerFactAttribute : FactAttribute
{
    public PartnerCenterBearerFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(System.Environment.GetEnvironmentVariable(
                "AGENTFORGE_LIVE_PARTNER_CENTER_BEARER_TOKEN")))
        {
            Skip = "Set AGENTFORGE_LIVE_PARTNER_CENTER_BEARER_TOKEN to run this credential-gated live gate.";
        }
    }
}
