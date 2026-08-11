using AgentForge.Domain.Agents;
using AgentForge.Domain.Orchestration;
using AgentForge.Domain.Primitives;

namespace AgentForge.UnitTests;

public sealed class OrchestrationTaskStateMachineTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 12, 0, 0, 0, TimeSpan.Zero);
    private const string HashA = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string HashB = "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string Token = "sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";

    [Fact]
    public void Sequential_dag_completes_without_repeating_finished_nodes()
    {
        var current = Create(Definition(
            Node("prepare"),
            Node("apply", ["prepare"]),
            Node("verify", ["apply"])));

        foreach (var id in new[] { "prepare", "apply", "verify" })
        {
            current = Claim(current, id);
            current = OrchestrationTaskStateMachine.Complete(
                current,
                new TaskNodeId(id),
                "worker",
                Token,
                HashB,
                current.UpdatedAt.AddSeconds(1)).Value;
        }

        Assert.Equal(OrchestrationTaskState.Completed, current.State);
        Assert.All(current.Nodes, node => Assert.Equal(TaskNodeState.Completed, node.State));
        Assert.True(OrchestrationTaskStateMachine.IsConsistent(current));
        var repeat = OrchestrationTaskStateMachine.Claim(
            current,
            new TaskNodeId("prepare"),
            "worker",
            Token,
            current.UpdatedAt.AddMinutes(1),
            current.UpdatedAt);
        Assert.False(repeat.IsSuccess);
    }

    [Fact]
    public void Concurrent_ready_nodes_obey_concurrency_and_exact_lease_authority()
    {
        var definition = Definition(Node("left"), Node("right")) with { MaximumConcurrency = 1 };
        var current = Create(definition);
        current = Claim(current, "left");

        var overflow = OrchestrationTaskStateMachine.Claim(
            current,
            new TaskNodeId("right"),
            "worker-2",
            HashB,
            current.UpdatedAt.AddMinutes(1),
            current.UpdatedAt);
        Assert.False(overflow.IsSuccess);
        Assert.Equal(FailureCode.ConcurrencyConflict, overflow.Failure!.Code);

        var stolen = OrchestrationTaskStateMachine.Complete(
            current,
            new TaskNodeId("left"),
            "other",
            Token,
            HashB,
            current.UpdatedAt.AddSeconds(1));
        Assert.False(stolen.IsSuccess);
        Assert.Equal(FailureCode.ConcurrencyConflict, stolen.Failure!.Code);
    }

    [Fact]
    public void Heartbeat_extends_only_a_live_exact_owned_lease()
    {
        var current = Claim(Create(Definition(Node("work"))), "work");
        var original = current.Nodes[0].Lease!;
        var heartbeat = OrchestrationTaskStateMachine.Heartbeat(
            current,
            new TaskNodeId("work"),
            "worker",
            Token,
            original.ExpiresAt.AddSeconds(30),
            current.UpdatedAt.AddSeconds(10));

        Assert.True(heartbeat.IsSuccess);
        Assert.True(heartbeat.Value.Nodes[0].Lease!.ExpiresAt > original.ExpiresAt);

        var stale = OrchestrationTaskStateMachine.Heartbeat(
            heartbeat.Value,
            new TaskNodeId("work"),
            "worker",
            HashB,
            heartbeat.Value.Nodes[0].Lease!.ExpiresAt.AddSeconds(30),
            heartbeat.Value.UpdatedAt.AddSeconds(1));
        Assert.False(stale.IsSuccess);
    }

    [Fact]
    public void Expired_lease_recovery_retries_then_dead_failure_is_not_reexecuted()
    {
        var definition = Definition(Node("work", retry: new TaskRetryPolicy(2, 0)));
        var first = Claim(Create(definition), "work");
        var recovered = OrchestrationTaskStateMachine.RecoverExpired(
            first,
            first.Nodes[0].Lease!.ExpiresAt);
        Assert.True(recovered.IsSuccess);
        Assert.Equal(TaskNodeState.Ready, recovered.Value.Nodes[0].State);

        var second = Claim(recovered.Value, "work");
        var exhausted = OrchestrationTaskStateMachine.RecoverExpired(
            second,
            second.Nodes[0].Lease!.ExpiresAt);
        Assert.True(exhausted.IsSuccess);
        Assert.Equal(TaskNodeState.Failed, exhausted.Value.Nodes[0].State);
        Assert.Equal(OrchestrationTaskState.Failed, exhausted.Value.State);
        Assert.Equal(2, exhausted.Value.Nodes[0].Attempt);
    }

    [Fact]
    public void Permanent_failure_runs_only_its_bound_compensation_before_terminal_failure()
    {
        var current = Create(Definition(
            Node("mutate", compensation: "undo"),
            Node("undo")));
        Assert.Equal(TaskNodeState.Pending, current.Nodes[1].State);
        current = Claim(current, "mutate");
        current = OrchestrationTaskStateMachine.Fail(
            current,
            new TaskNodeId("mutate"),
            "worker",
            Token,
            HashB,
            FailureCode.RecoverableExternalFailure,
            retryable: false,
            current.UpdatedAt.AddSeconds(1)).Value;

        Assert.Equal(TaskNodeState.Ready, current.Nodes[1].State);
        Assert.Equal(OrchestrationTaskState.Waiting, current.State);
        current = Claim(current, "undo");
        current = OrchestrationTaskStateMachine.Complete(
            current,
            new TaskNodeId("undo"),
            "worker",
            Token,
            HashB,
            current.UpdatedAt.AddSeconds(1)).Value;
        Assert.Equal(TaskNodeState.Compensated, current.Nodes[1].State);
        Assert.Equal(OrchestrationTaskState.Failed, current.State);
    }

    [Fact]
    public void Definition_rejects_cycles_unknown_dependencies_and_duplicate_capabilities()
    {
        var cycle = Definition(Node("one", ["two"]), Node("two", ["one"]));
        Assert.False(OrchestrationTaskStateMachine.ValidateDefinition(cycle).IsSuccess);

        var unknown = Definition(Node("one", ["missing"]));
        Assert.False(OrchestrationTaskStateMachine.ValidateDefinition(unknown).IsSuccess);

        var duplicate = Definition(Node("one") with { RequiredCapabilities = ["tool:read", "tool:read"] });
        Assert.False(OrchestrationTaskStateMachine.ValidateDefinition(duplicate).IsSuccess);
    }

    [Fact]
    public void Mutable_definition_input_is_snapshotted_and_hash_tampering_is_detected()
    {
        var dependencies = new List<TaskNodeId>();
        var definition = Definition(Node("one") with { Dependencies = dependencies });
        var current = Create(definition);
        dependencies.Add(new TaskNodeId("injected"));

        Assert.Empty(current.Definition.Nodes[0].Dependencies);
        Assert.True(OrchestrationTaskStateMachine.IsConsistent(current));
        Assert.False(OrchestrationTaskStateMachine.IsConsistent(current with { Version = 99 }));
    }

    private static OrchestrationTaskSnapshot Claim(OrchestrationTaskSnapshot current, string id) =>
        OrchestrationTaskStateMachine.Claim(
            current,
            new TaskNodeId(id),
            "worker",
            Token,
            current.UpdatedAt.AddMinutes(1),
            current.UpdatedAt).Value;

    private static OrchestrationTaskSnapshot Create(OrchestrationTaskDefinition definition) =>
        OrchestrationTaskStateMachine.Create(
            definition,
            new ActorId("operator"),
            "task-key",
            new CorrelationId("task-correlation"),
            null,
            Now).Value;

    private static OrchestrationTaskDefinition Definition(params TaskNodeDefinition[] nodes) => new(
        new OrchestrationTaskId(Guid.Parse("10000000-0000-0000-0000-000000000001")),
        new InstallationId(Guid.Parse("20000000-0000-0000-0000-000000000002")),
        new AgentIdentityId(Guid.Parse("30000000-0000-0000-0000-000000000003")),
        1,
        OrchestrationPattern.Sequential,
        nodes,
        Math.Min(2, nodes.Length),
        4,
        8,
        HashA,
        HashA,
        HashA);

    private static TaskNodeDefinition Node(
        string id,
        string[]? dependencies = null,
        TaskRetryPolicy? retry = null,
        string? compensation = null) => new(
        new TaskNodeId(id),
        id,
        (dependencies ?? []).Select(value => new TaskNodeId(value)).ToArray(),
        ["tool:read"],
        [HashA],
        new TaskExecutionBudget(4, 1_000, 1_000, 60),
        retry ?? new TaskRetryPolicy(1, 0),
        compensation is null ? null : new TaskNodeId(compensation));
}
