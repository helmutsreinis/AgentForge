using AgentForge.Domain.Agents;
using AgentForge.Domain.Orchestration;
using AgentForge.Domain.Primitives;

namespace AgentForge.UnitTests;

public sealed class DelegationAuthorityEvaluatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 12, 2, 0, 0, TimeSpan.Zero);
    private const string HashA = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string HashB = "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Fact]
    public void Grant_is_minimum_context_capability_intersection_and_clamped_budget()
    {
        var result = DelegationAuthorityEvaluator.Evaluate(
            Parent(),
            Request(
                requestedCapabilities: ["tool:write", "tool:read", "tool:absent"],
                requiredCapabilities: ["tool:read"],
                context: [HashB],
                budget: new TaskExecutionBudget(20, 8_000, 9_000, 600)),
            Now);

        Assert.True(result.IsSuccess, result.Failure?.Message);
        Assert.Equal(["tool:read", "tool:write"], result.Value.CapabilityIds);
        Assert.Equal([HashB], result.Value.ContextEvidenceHashes);
        Assert.Equal(new TaskExecutionBudget(4, 2_000, 1_500, 120), result.Value.Budget);
        Assert.Equal(3, result.Value.Depth);
        Assert.True(DelegationAuthorityEvaluator.IsConsistent(result.Value));
    }

    [Fact]
    public void Required_capability_outside_parent_fails_instead_of_silent_escalation()
    {
        var result = DelegationAuthorityEvaluator.Evaluate(
            Parent(),
            Request(["device:write"], ["device:write"], [HashA], Budget()),
            Now);

        Assert.False(result.IsSuccess);
        Assert.Equal(FailureCode.PolicyDenied, result.Failure!.Code);
    }

    [Fact]
    public void Context_outside_explicit_parent_evidence_is_denied()
    {
        var result = DelegationAuthorityEvaluator.Evaluate(
            Parent(),
            Request(["tool:read"], [], ["sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc"], Budget()),
            Now);

        Assert.False(result.IsSuccess);
        Assert.Equal(FailureCode.PolicyDenied, result.Failure!.Code);
    }

    [Theory]
    [InlineData(4, 1, 0)]
    [InlineData(2, 8, 0)]
    [InlineData(2, 1, 2)]
    public void Depth_total_count_and_concurrency_are_independent_hard_bounds(
        int depth,
        int spawned,
        int active)
    {
        var parent = Parent() with
        {
            CurrentDepth = depth,
            SpawnedChildren = spawned,
            ActiveChildren = active,
        };
        var result = DelegationAuthorityEvaluator.Evaluate(parent, Request(), Now);

        Assert.False(result.IsSuccess);
        Assert.Equal(FailureCode.PolicyDenied, result.Failure!.Code);
    }

    [Fact]
    public void Expired_authority_zero_budget_duplicates_and_tampered_grant_fail_closed()
    {
        Assert.False(DelegationAuthorityEvaluator.Evaluate(
            Parent() with { ExpiresAt = Now }, Request(), Now).IsSuccess);
        Assert.False(DelegationAuthorityEvaluator.Evaluate(
            Parent() with { RemainingBudget = new TaskExecutionBudget(0, 0, 0, 0) }, Request(), Now).IsSuccess);
        Assert.False(DelegationAuthorityEvaluator.Evaluate(
            Parent() with { CapabilityIds = ["tool:read", "tool:read"] }, Request(), Now).IsSuccess);

        var grant = DelegationAuthorityEvaluator.Evaluate(Parent(), Request(), Now).Value;
        Assert.False(DelegationAuthorityEvaluator.IsConsistent(grant with { Depth = 9 }));
    }

    [Fact]
    public void Caller_collections_are_snapshotted_in_sorted_grant()
    {
        var requested = new List<string> { "tool:write", "tool:read" };
        var context = new List<string> { HashB, HashA };
        var result = DelegationAuthorityEvaluator.Evaluate(
            Parent(),
            Request(requested, ["tool:read"], context, Budget()),
            Now);
        requested.Add("credential:read");
        context.Clear();

        Assert.True(result.IsSuccess);
        Assert.Equal(["tool:read", "tool:write"], result.Value.CapabilityIds);
        Assert.Equal([HashA, HashB], result.Value.ContextEvidenceHashes);
        Assert.DoesNotContain("credential:read", result.Value.CapabilityIds);
    }

    private static ParentDelegationAuthority Parent() => new(
        new OrchestrationTaskId(Guid.Parse("40000000-0000-0000-0000-000000000001")),
        new InstallationId(Guid.Parse("40000000-0000-0000-0000-000000000002")),
        new AgentIdentityId(Guid.Parse("40000000-0000-0000-0000-000000000003")),
        4,
        2,
        1,
        0,
        4,
        8,
        2,
        ["tool:read", "tool:write"],
        [HashA, HashB],
        new TaskExecutionBudget(8, 10_000, 10_000, 300),
        new TaskExecutionBudget(4, 2_000, 1_500, 120),
        HashA,
        HashB,
        Now.AddMinutes(5));

    private static ChildDelegationRequest Request(
        IReadOnlyList<string>? requestedCapabilities = null,
        IReadOnlyList<string>? requiredCapabilities = null,
        IReadOnlyList<string>? context = null,
        TaskExecutionBudget? budget = null) => new(
        new ChildDelegationId(Guid.Parse("40000000-0000-0000-0000-000000000004")),
        new AgentIdentityId(Guid.Parse("40000000-0000-0000-0000-000000000005")),
        1,
        DelegationRole.Worker,
        requestedCapabilities ?? ["tool:read"],
        requiredCapabilities ?? ["tool:read"],
        context ?? [HashA],
        budget ?? Budget(),
        HashB,
        new CorrelationId("delegation-correlation"),
        new CorrelationId("delegation-causation"));

    private static TaskExecutionBudget Budget() => new(4, 1_000, 1_000, 60);
}
