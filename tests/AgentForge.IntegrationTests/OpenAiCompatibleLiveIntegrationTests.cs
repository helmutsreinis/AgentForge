using AgentForge.Abstractions.Models;
using AgentForge.Abstractions.Time;
using AgentForge.Domain.Models;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Providers;
using AgentForge.Models;
using AgentForge.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AgentForge.IntegrationTests;

public sealed class OpenAiCompatibleLiveIntegrationTests
{
    private const string EndpointVariable = "AGENTFORGE_LIVE_OPENAI_COMPATIBLE_ENDPOINT";
    private const string ModelVariable = "AGENTFORGE_LIVE_OPENAI_COMPATIBLE_MODEL";

    [CredentialFreeLiveFact]
    [Trait("Category", "Live")]
    public async Task Configured_credential_free_endpoint_streams_through_the_public_adapter()
    {
        var endpointValue = global::System.Environment.GetEnvironmentVariable(EndpointVariable);
        var model = global::System.Environment.GetEnvironmentVariable(ModelVariable);
        Assert.False(string.IsNullOrWhiteSpace(endpointValue));
        Assert.False(string.IsNullOrWhiteSpace(model));

        Assert.True(Uri.TryCreate(endpointValue!, UriKind.Absolute, out var endpoint));
        Assert.True(endpoint.Scheme is "http" or "https");
        Assert.True(string.IsNullOrEmpty(endpoint.UserInfo));
        Assert.True(string.IsNullOrEmpty(endpoint.Query));
        Assert.True(string.IsNullOrEmpty(endpoint.Fragment));
        Assert.True(model!.Length <= 256 && model.All(character => !char.IsControl(character)));

        var now = DateTimeOffset.UtcNow;
        var descriptor = new ModelProviderDescriptor(
            ProviderProfileId.New(),
            "openai-compatible",
            model,
            [
                Evidence(ModelCapability.TextGeneration, now),
                Evidence(ModelCapability.Streaming, now),
            ]);
        using var provider = OpenAiCompatibleModelProvider.Create(
            descriptor,
            new OpenAiCompatibleModelProviderOptions(
                endpoint,
                AllowInsecureHttp: endpoint.Scheme == "http",
                DisableThinking: true),
            CreateContextPreparer(),
            new LiveClock()).Value;
        var request = new ModelRequest(
            new ModelRequestId(Guid.NewGuid()),
            model,
            [new ModelMessage(ModelMessageRole.User, [new ModelTextContent("Reply exactly AGENTFORGE_QWEN_OK.")])],
            [],
            new ModelResponseFormat(ModelResponseFormatKind.Text),
            new ModelInvocationLimits(32, 0, 32, 30),
            0,
            1,
            42,
            new CorrelationId("agentforge-openai-compatible-live-gate"));

        var events = new List<ModelStreamEvent>();
        await foreach (var item in provider.StreamAsync(request, CancellationToken.None))
        {
            events.Add(item);
        }

        Assert.Equal(
            "AGENTFORGE_QWEN_OK",
            string.Concat(events.OfType<ModelTextDeltaEvent>().Select(item => item.Delta)));
        var started = Assert.Single(events.OfType<ModelStartedEvent>());
        Assert.Equal(ModelContextPreparer.PolicyName, started.ContextPreparationPolicy);
        Assert.Equal(0, started.ContextRedactionCount);
        Assert.Single(events.OfType<ModelUsageEvent>());
        Assert.Equal(ModelFinishReason.Stop, Assert.Single(events.OfType<ModelCompletedEvent>()).FinishReason);
        Assert.DoesNotContain(events, item => item is ModelErrorEvent);
    }

    private static ModelCapabilityEvidence Evidence(ModelCapability capability, DateTimeOffset now) => new(
        capability,
        ModelCapabilityEvidenceSource.Probed,
        ModelCapabilityAvailability.Available,
        "Explicit credential-free live integration gate.",
        now.AddSeconds(-1),
        now.AddMinutes(5));

    private sealed class LiveClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }

    private static IModelContextPreparer CreateContextPreparer()
    {
        var services = new ServiceCollection();
        services.AddAgentForgeSecurity(new ConfigurationBuilder().Build());
        services.AddAgentForgeModels();
        using var provider = services.BuildServiceProvider(validateScopes: true);
        return provider.GetRequiredService<IModelContextPreparer>();
    }
}

internal sealed class CredentialFreeLiveFactAttribute : FactAttribute
{
    public CredentialFreeLiveFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(global::System.Environment.GetEnvironmentVariable(
                "AGENTFORGE_LIVE_OPENAI_COMPATIBLE_ENDPOINT")) ||
            string.IsNullOrWhiteSpace(global::System.Environment.GetEnvironmentVariable(
                "AGENTFORGE_LIVE_OPENAI_COMPATIBLE_MODEL")))
        {
            Skip = "Set the OpenAI-compatible endpoint and model environment variables to run this live gate.";
        }
    }
}
