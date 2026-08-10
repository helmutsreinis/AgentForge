using AgentForge.Abstractions.Agents;
using AgentForge.Domain.Agents;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Providers;
using AgentForge.Domain.Security;
using AgentForge.Setup;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AgentForge.UnitTests;

public sealed class AgentDefinitionEvaluatorTests
{
    private static readonly ProviderProfileId ProviderId = new(Guid.Parse("eb32841a-f86f-4085-a5d7-64d08b6fc690"));

    [Fact]
    public void Produces_normalized_fail_closed_effective_policy()
    {
        using var services = BuildServices();
        var evaluator = services.GetRequiredService<IAgentDefinitionEvaluator>();
        var candidate = CreateCandidate() with
        {
            Name = "  Architect  ",
            CapabilityPolicy = new AgentCapabilityPolicy(
                NetworkPosture.LoopbackOnly,
                ["tool:repo.read", "tool:repo.read"],
                ["skill:csharp.review"]),
        };

        var normalized = evaluator.NormalizeAndValidate(candidate);
        Assert.True(normalized.IsSuccess);
        Assert.Equal("Architect", normalized.Value.Name);
        Assert.Equal(["tool:repo.read"], normalized.Value.CapabilityPolicy.ToolGrants);

        var effective = evaluator.Evaluate(normalized.Value, CreateProvider());
        Assert.True(effective.IsSuccess);
        AssertDecision(effective.Value, "model.text", CapabilityDecision.Allow);
        AssertDecision(effective.Value, "model.tool-calls", CapabilityDecision.RequireApproval);
        AssertDecision(effective.Value, "tool:repo.read", CapabilityDecision.RequireApproval);
        AssertDecision(effective.Value, "network.external", CapabilityDecision.Deny);
        AssertDecision(effective.Value, "credentials.materialize", CapabilityDecision.Deny);
        AssertDecision(effective.Value, "device.write", CapabilityDecision.Deny);
        AssertDecision(effective.Value, "learning.propose", CapabilityDecision.Allow);
        AssertDecision(effective.Value, "learning.promote", CapabilityDecision.Deny);
    }

    [Fact]
    public void Rejects_child_budget_that_exceeds_parent_before_policy_preview()
    {
        using var services = BuildServices();
        var evaluator = services.GetRequiredService<IAgentDefinitionEvaluator>();
        var candidate = CreateCandidate() with
        {
            ChildLimits = new ChildAgentLimits(2, 2, 1, 20_001),
        };

        var result = evaluator.NormalizeAndValidate(candidate);

        Assert.False(result.IsSuccess);
        Assert.Equal(FailureCode.PolicyDenied, result.Failure?.Code);
    }

    [Fact]
    public void Rejects_cloud_fallback_for_local_only_policy()
    {
        using var services = BuildServices();
        var evaluator = services.GetRequiredService<IAgentDefinitionEvaluator>();
        var candidate = CreateCandidate() with
        {
            ModelPolicy = new AgentModelPolicy(ProviderId, ModelDataLocality.LocalOnly, AllowFallback: true),
        };

        var result = evaluator.NormalizeAndValidate(candidate);

        Assert.False(result.IsSuccess);
        Assert.Equal(FailureCode.PolicyDenied, result.Failure?.Code);
    }

    [Fact]
    public void Local_only_policy_rejects_non_loopback_provider()
    {
        using var services = BuildServices();
        var evaluator = services.GetRequiredService<IAgentDefinitionEvaluator>();
        var normalized = evaluator.NormalizeAndValidate(CreateCandidate());
        Assert.True(normalized.IsSuccess);
        var provider = CreateProvider() with { Endpoint = new Uri("https://provider.example/v1") };

        var result = evaluator.Evaluate(normalized.Value, provider);

        Assert.False(result.IsSuccess);
        Assert.Equal(FailureCode.PolicyDenied, result.Failure?.Code);
    }

    internal static AgentIdentityCandidate CreateCandidate() => new(
        "Architect",
        "C# systems architecture",
        "Design bounded and verifiable systems.",
        "en",
        "UTC",
        "Concise",
        null,
        new AgentModelPolicy(ProviderId, ModelDataLocality.LocalOnly, AllowFallback: false),
        new AgentMemoryPolicy(AgentMemoryScope.Agent, 30),
        new AgentCapabilityPolicy(NetworkPosture.LoopbackOnly, [], []),
        new AgentBudget(64, 32, 16_000, 4_000, 3600),
        new ChildAgentLimits(2, 4, 2, 10_000),
        new AgentLearningPolicy(LearningMode.Propose, MutableSkillScope.ProposalWorkspaceOnly));

    internal static ProviderProfile CreateProvider(InstallationId? installationId = null) => new(
        ProviderId,
        installationId ?? new InstallationId(Guid.Parse("6cc11851-b8ab-475a-bb61-f06daecef7dd")),
        "primary",
        "deterministic",
        new Uri("http://127.0.0.1:9000/v1"),
        "deterministic-text-v1",
        new SecretReference("fixture", "reference"),
        new ProviderCapabilitySummary(true, true, true, false, "fixture"),
        0,
        DateTimeOffset.UnixEpoch,
        DateTimeOffset.UnixEpoch,
        new ActorId("operator"),
        new CorrelationId("fixture"));

    private static ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();
        services.AddAgentForgeSetup(new ConfigurationBuilder().Build());
        return services.BuildServiceProvider();
    }

    private static void AssertDecision(
        EffectiveAgentDefinition definition,
        string capabilityId,
        CapabilityDecision expected)
    {
        var capability = Assert.Single(definition.Capabilities, item => item.CapabilityId == capabilityId);
        Assert.Equal(expected, capability.Decision);
    }
}
