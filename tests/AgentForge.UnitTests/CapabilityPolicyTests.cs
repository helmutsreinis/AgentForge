using AgentForge.Abstractions.Security;
using AgentForge.Domain.Agents;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Security;
using AgentForge.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AgentForge.UnitTests;

public sealed class CapabilityPolicyTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 11, 4, 30, 0, TimeSpan.Zero);
    private static readonly InstallationId InstallationId = new(Guid.Parse("629a1a64-3aa5-4257-8bf9-b0d44f3abcf4"));
    private static readonly AgentIdentityId AgentId = new(Guid.Parse("24d89d17-48d1-429c-9236-cb1432b277a4"));
    private readonly ServiceProvider _services;

    public CapabilityPolicyTests()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAgentForgeSecurity(new ConfigurationBuilder().Build());
        _services = services.BuildServiceProvider(validateScopes: true);
    }

    [Fact]
    public void Context_canonicalizes_parameter_order_and_binds_every_exact_dimension()
    {
        var factory = _services.GetRequiredService<IAuthorizationContextFactory>();
        var first = factory.Create(CreateRequest("""{"z":2,"nested":{"b":true,"a":"x"},"a":1}"""));
        var second = factory.Create(CreateRequest(""" { "a": 1, "nested": { "a": "x", "b": true }, "z": 2 } """));

        Assert.True(first.IsSuccess, first.Failure?.Message);
        Assert.True(second.IsSuccess, second.Failure?.Message);
        Assert.Equal("{\"a\":1,\"nested\":{\"a\":\"x\",\"b\":true},\"z\":2}", first.Value.CanonicalParametersJson);
        Assert.Equal(first.Value.ParametersHash, second.Value.ParametersHash);
        Assert.Equal(first.Value.RequestHash, second.Value.RequestHash);
        Assert.StartsWith("sha256:", first.Value.TargetHash, StringComparison.Ordinal);
        Assert.StartsWith("sha256:", first.Value.WorkspaceHash, StringComparison.Ordinal);

        var changedToolVersion = factory.Create(CreateRequest("{\"a\":1}") with { ToolVersion = "2.0.0" });
        var changedDescriptor = factory.Create(CreateRequest("{\"a\":1}") with
        {
            ToolDescriptorHash = "sha256:" + new string('c', 64),
        });
        var baseline = factory.Create(CreateRequest("{\"a\":1}"));
        Assert.NotEqual(baseline.Value.RequestHash, changedToolVersion.Value.RequestHash);
        Assert.NotEqual(baseline.Value.RequestHash, changedDescriptor.Value.RequestHash);
    }

    [Theory]
    [InlineData("{\"a\":1,\"a\":2}")]
    [InlineData("[]")]
    [InlineData("not-json")]
    public void Context_rejects_ambiguous_or_non_object_parameters(string parameters)
    {
        var result = _services.GetRequiredService<IAuthorizationContextFactory>()
            .Create(CreateRequest(parameters));

        Assert.False(result.IsSuccess);
        Assert.Equal(FailureCode.ValidationFailure, result.Failure?.Code);
    }

    [Fact]
    public void Context_rejects_undefined_risk_class()
    {
        var result = _services.GetRequiredService<IAuthorizationContextFactory>()
            .Create(CreateRequest("{}") with { RiskClass = (CapabilityRiskClass)999 });

        Assert.False(result.IsSuccess);
        Assert.Equal(FailureCode.ValidationFailure, result.Failure?.Code);
    }

    [Fact]
    public void Context_rejects_partial_or_malformed_tool_descriptor_identity()
    {
        var factory = _services.GetRequiredService<IAuthorizationContextFactory>();
        var missingHash = factory.Create(CreateRequest("{}") with { ToolDescriptorHash = null });
        var missingVersion = factory.Create(CreateRequest("{}") with { ToolVersion = null });
        var malformedHash = factory.Create(CreateRequest("{}") with { ToolDescriptorHash = "sha256:invalid" });

        Assert.Equal(FailureCode.ValidationFailure, missingHash.Failure?.Code);
        Assert.Equal(FailureCode.ValidationFailure, missingVersion.Failure?.Code);
        Assert.Equal(FailureCode.ValidationFailure, malformedHash.Failure?.Code);
    }

    [Fact]
    public void Missing_and_ambiguous_policy_fail_closed()
    {
        var context = CreateContext();
        var evaluator = _services.GetRequiredService<ICapabilityPolicyEvaluator>();
        var missing = evaluator.Evaluate(
            context,
            CreatePolicy([]),
            null,
            Now);
        var duplicateRules = new[]
        {
            Rule(CapabilityDecision.Allow),
            Rule(CapabilityDecision.RequireApproval),
        };
        var ambiguous = evaluator.Evaluate(context, CreatePolicy(duplicateRules), null, Now);
        var invalid = evaluator.Evaluate(
            context,
            CreatePolicy([Rule((CapabilityDecision)999)]),
            null,
            Now);

        Assert.Equal(CapabilityDecision.Deny, missing.Decision);
        Assert.Equal(CapabilityDecision.Deny, ambiguous.Decision);
        Assert.Equal(CapabilityDecision.Deny, invalid.Decision);
    }

    [Fact]
    public void Exact_active_grant_allows_but_expired_consumed_or_changed_requests_do_not()
    {
        var context = CreateContext();
        var evaluator = _services.GetRequiredService<ICapabilityPolicyEvaluator>();
        var policy = CreatePolicy([Rule(CapabilityDecision.RequireApproval)]);
        var approval = CreateApproval(context, CapabilityApprovalDisposition.Grant);

        Assert.Equal(CapabilityDecision.RequireApproval, evaluator.Evaluate(context, policy, null, Now).Decision);
        Assert.Equal(CapabilityDecision.Allow, evaluator.Evaluate(context, policy, approval, Now).Decision);
        Assert.Equal(
            CapabilityDecision.RequireApproval,
            evaluator.Evaluate(context, policy, approval, approval.ExpiresAt).Decision);
        var consumed = CapabilityApprovalStateMachine.Consume(approval, context.RequestHash, Now.AddMinutes(1));
        Assert.True(consumed.IsSuccess);
        Assert.Equal(
            CapabilityDecision.RequireApproval,
            evaluator.Evaluate(context, policy, consumed.Value, Now.AddMinutes(2)).Decision);

        var changedContext = CreateContext("{\"path\":\"different\"}");
        Assert.Equal(
            CapabilityDecision.RequireApproval,
            evaluator.Evaluate(changedContext, policy, approval, Now).Decision);
        var changedDescriptor = _services.GetRequiredService<IAuthorizationContextFactory>()
            .Create(CreateRequest("{\"path\":\"src\"}") with
            {
                ToolDescriptorHash = "sha256:" + new string('c', 64),
            });
        Assert.Equal(
            CapabilityDecision.RequireApproval,
            evaluator.Evaluate(changedDescriptor.Value, policy, approval, Now).Decision);
        var changedInstallationVersion = _services.GetRequiredService<IAuthorizationContextFactory>()
            .Create(CreateRequest("{\"path\":\"src\"}") with { InstallationVersion = 8 });
        Assert.Equal(
            CapabilityDecision.Deny,
            evaluator.Evaluate(changedInstallationVersion.Value, policy, approval, Now).Decision);
    }

    [Fact]
    public void Exact_active_denial_vetoes_and_parent_child_intersection_is_most_restrictive()
    {
        var context = CreateContext();
        var evaluator = _services.GetRequiredService<ICapabilityPolicyEvaluator>();
        var policy = CreatePolicy([Rule(CapabilityDecision.RequireApproval)]);
        var denial = CreateApproval(context, CapabilityApprovalDisposition.Deny);

        Assert.Equal(CapabilityDecision.Deny, evaluator.Evaluate(context, policy, denial, Now).Decision);

        var parent = CreatePolicy([
            Rule(CapabilityDecision.Allow),
            new CapabilityPolicyRule("tool:parent-only", CapabilityRiskClass.Read, CapabilityDecision.Allow, "parent"),
        ]);
        var child = CreatePolicy([Rule(CapabilityDecision.RequireApproval)]);
        var intersection = evaluator.Intersect(parent, child);

        Assert.Equal(
            CapabilityDecision.RequireApproval,
            Assert.Single(intersection.Rules, item => item.CapabilityId == "tool:repo.read").Decision);
        Assert.Equal(
            CapabilityDecision.Deny,
            Assert.Single(intersection.Rules, item => item.CapabilityId == "tool:parent-only").Decision);
    }

    [Fact]
    public void Approval_state_machine_rejects_replay_and_invalid_transitions()
    {
        var context = CreateContext();
        var approval = CreateApproval(context, CapabilityApprovalDisposition.Grant);
        var wrongRequest = CapabilityApprovalStateMachine.Consume(
            approval,
            "sha256:" + new string('0', 64),
            Now.AddMinutes(1));
        var beforeCreation = CapabilityApprovalStateMachine.Consume(
            approval,
            context.RequestHash,
            Now.AddMinutes(-1));
        var consumed = CapabilityApprovalStateMachine.Consume(approval, context.RequestHash, Now.AddMinutes(1));
        var replay = CapabilityApprovalStateMachine.Consume(consumed.Value, context.RequestHash, Now.AddMinutes(2));
        var revokeConsumed = CapabilityApprovalStateMachine.Revoke(consumed.Value, Now.AddMinutes(2));

        Assert.Equal(FailureCode.PolicyDenied, wrongRequest.Failure?.Code);
        Assert.Equal(FailureCode.PolicyDenied, beforeCreation.Failure?.Code);
        Assert.Equal(CapabilityApprovalState.Consumed, consumed.Value.State);
        Assert.Equal(FailureCode.PolicyDenied, replay.Failure?.Code);
        Assert.Equal(FailureCode.InvalidStateTransition, revokeConsumed.Failure?.Code);
    }

    public void Dispose() => _services.Dispose();

    private AuthorizationContext CreateContext(string parameters = "{\"path\":\"src\"}")
    {
        var result = _services.GetRequiredService<IAuthorizationContextFactory>()
            .Create(CreateRequest(parameters));
        Assert.True(result.IsSuccess, result.Failure?.Message);
        return result.Value;
    }

    private static CapabilityInvocationRequest CreateRequest(string parameters) => new(
        InstallationId,
        7,
        AgentId,
        3,
        new ActorId("worker"),
        "tool:repo.read",
        CapabilityRiskClass.Read,
        "repo.read",
        "1.0.0",
        "sha256:" + new string('d', 64),
        parameters,
        AuthorizationTargetKind.FileSystemPath,
        Path.Combine(Path.GetTempPath(), "agentforge-policy-target"),
        Path.GetTempPath(),
        new CorrelationId("policy-test"));

    private static CapabilityPolicyRule Rule(CapabilityDecision decision) =>
        new("tool:repo.read", CapabilityRiskClass.Read, decision, "fixture");

    private static CapabilityPolicySnapshot CreatePolicy(IReadOnlyList<CapabilityPolicyRule> rules) => new(
        InstallationId,
        7,
        AgentId,
        3,
        rules,
        "sha256:" + new string('a', 64));

    private static CapabilityApproval CreateApproval(
        AuthorizationContext context,
        CapabilityApprovalDisposition disposition)
    {
        var result = CapabilityApprovalStateMachine.Create(
            new CapabilityApprovalId(Guid.Parse("e4a26dfb-0c01-48c4-ab85-e4357c242f66")),
            context,
            disposition,
            Now,
            Now.AddMinutes(10),
            new ActorId("administrator"),
            new CorrelationId("approval-test"),
            "sha256:" + new string('b', 64),
            "approval-test-idempotency");
        Assert.True(result.IsSuccess, result.Failure?.Message);
        return result.Value;
    }
}
