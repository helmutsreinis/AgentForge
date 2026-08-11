using System.Net;
using AgentForge.Domain.Models;
using AgentForge.Models;

namespace AgentForge.UnitTests;

public sealed class EndpointDestinationPolicyTests
{
    [Fact]
    public void Loopback_policy_requires_every_resolved_address_to_remain_loopback()
    {
        Assert.True(EndpointDestinationPolicy.Allows(
            ModelProviderDataLocation.Loopback,
            [IPAddress.Loopback, IPAddress.IPv6Loopback]));
        Assert.False(EndpointDestinationPolicy.Allows(
            ModelProviderDataLocation.Loopback,
            [IPAddress.Loopback, IPAddress.Parse("192.168.1.89")]));
    }

    [Fact]
    public void Private_policy_rejects_public_loopback_link_local_and_mixed_answers()
    {
        Assert.True(EndpointDestinationPolicy.Allows(
            ModelProviderDataLocation.PrivateNetwork,
            [IPAddress.Parse("10.0.0.2"), IPAddress.Parse("172.31.2.4"), IPAddress.Parse("192.168.1.89")]));
        Assert.True(EndpointDestinationPolicy.Allows(
            ModelProviderDataLocation.PrivateNetwork,
            [IPAddress.Parse("fd12:3456::1")]));
        Assert.False(EndpointDestinationPolicy.Allows(
            ModelProviderDataLocation.PrivateNetwork,
            [IPAddress.Parse("192.168.1.89"), IPAddress.Parse("8.8.8.8")]));
        Assert.False(EndpointDestinationPolicy.Allows(
            ModelProviderDataLocation.PrivateNetwork,
            [IPAddress.Loopback]));
        Assert.False(EndpointDestinationPolicy.Allows(
            ModelProviderDataLocation.PrivateNetwork,
            [IPAddress.Parse("169.254.1.1")]));
    }

    [Fact]
    public void Cloud_policy_accepts_only_global_answers_and_blocks_rebinding_shapes()
    {
        Assert.True(EndpointDestinationPolicy.Allows(
            ModelProviderDataLocation.Cloud,
            [IPAddress.Parse("8.8.8.8"), IPAddress.Parse("1.1.1.1")]));
        Assert.True(EndpointDestinationPolicy.Allows(
            ModelProviderDataLocation.Cloud,
            [IPAddress.Parse("2606:4700:4700::1111")]));
        Assert.False(EndpointDestinationPolicy.Allows(
            ModelProviderDataLocation.Cloud,
            [IPAddress.Parse("8.8.8.8"), IPAddress.Loopback]));
        Assert.False(EndpointDestinationPolicy.Allows(
            ModelProviderDataLocation.Cloud,
            [IPAddress.Any]));
        Assert.False(EndpointDestinationPolicy.Allows(
            ModelProviderDataLocation.Cloud,
            [IPAddress.Parse("203.0.113.5")]));
        Assert.False(EndpointDestinationPolicy.Allows(
            ModelProviderDataLocation.Cloud,
            []));
    }

    [Fact]
    public void Inference_is_conservative_and_explicit_location_cannot_be_in_process()
    {
        Assert.Equal(
            ModelProviderDataLocation.Loopback,
            EndpointDestinationPolicy.Infer(new Uri("http://localhost:8000/v1/chat/completions")));
        Assert.Equal(
            ModelProviderDataLocation.PrivateNetwork,
            EndpointDestinationPolicy.Infer(new Uri("http://192.168.1.89:8000/v1/chat/completions")));
        Assert.Equal(
            ModelProviderDataLocation.Cloud,
            EndpointDestinationPolicy.Infer(new Uri("https://api.example.com/v1/chat/completions")));

        var rejected = OpenAiCompatibleModelProvider.Create(
            Descriptor(),
            new OpenAiCompatibleModelProviderOptions(
                new Uri("https://api.example.com/v1/chat/completions"),
                DestinationDataLocation: ModelProviderDataLocation.InProcess),
            new PassthroughContextPreparer(),
            new FixedClock());
        Assert.False(rejected.IsSuccess);
    }

    private static ModelProviderDescriptor Descriptor() => new(
        new AgentForge.Domain.Providers.ProviderProfileId(Guid.NewGuid()),
        "openai-compatible",
        "fixture",
        [
            Evidence(ModelCapability.TextGeneration),
            Evidence(ModelCapability.Streaming),
        ]);

    private static ModelCapabilityEvidence Evidence(ModelCapability capability) => new(
        capability,
        ModelCapabilityEvidenceSource.Declared,
        ModelCapabilityAvailability.Available,
        "fixture",
        DateTimeOffset.UtcNow);

    private sealed class PassthroughContextPreparer : AgentForge.Abstractions.Models.IModelContextPreparer
    {
        public AgentForge.Domain.Primitives.DomainResult<PreparedModelContext> Prepare(ModelRequest request) =>
            AgentForge.Domain.Primitives.DomainResult.Success(new PreparedModelContext(
                request,
                0,
                ModelContextPreparer.PolicyName,
                "sha256:" + new string('a', 64)));
    }

    private sealed class FixedClock : AgentForge.Abstractions.Time.IClock
    {
        public DateTimeOffset UtcNow { get; } = DateTimeOffset.UtcNow;
    }
}
