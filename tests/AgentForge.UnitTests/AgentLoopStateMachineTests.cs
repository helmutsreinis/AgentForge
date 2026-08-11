using AgentForge.Domain.Agents;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Runtime;

namespace AgentForge.UnitTests;

public sealed class AgentLoopStateMachineTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 11, 20, 0, 0, TimeSpan.Zero);
    private const string InitialHash = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string StepHash = "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string ProgressHash = "sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
    private const string NextProgressHash = "sha256:dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd";

    [Fact]
    public void Complete_loop_visits_all_typed_phases_and_hash_chains_immutable_snapshots()
    {
        var snapshots = new List<AgentLoopSnapshot> { Create() };

        foreach (var phase in Enum.GetValues<AgentLoopPhase>())
        {
            Assert.Equal(phase, snapshots[^1].Phase);
            var advanced = AgentLoopStateMachine.Advance(
                snapshots[^1],
                Step(
                    progress: phase is AgentLoopPhase.Persist ? ProgressHash : null,
                    inputTokens: 10,
                    outputTokens: 5,
                    toolCalls: phase is AgentLoopPhase.Act ? 1 : 0,
                    complete: phase is AgentLoopPhase.Verify),
                Now.AddSeconds(snapshots.Count));

            Assert.True(advanced.IsSuccess, advanced.Failure?.Message);
            snapshots.Add(advanced.Value);
        }

        Assert.Equal(AgentLoopState.Completed, snapshots[^1].State);
        Assert.Equal(60, snapshots[^1].Consumption.InputTokens);
        Assert.Equal(30, snapshots[^1].Consumption.OutputTokens);
        Assert.Equal(1, snapshots[^1].Consumption.ToolCalls);
        Assert.True(snapshots[^1].CompletionPending);
        Assert.All(snapshots, snapshot => Assert.True(AgentLoopStateMachine.IsConsistent(snapshot)));
        for (var index = 1; index < snapshots.Count; index++)
        {
            Assert.Equal(snapshots[index - 1].SnapshotHash, snapshots[index].PreviousSnapshotHash);
            Assert.Equal(index, snapshots[index].Sequence);
        }
    }

    [Fact]
    public void Invalid_structured_output_repeats_the_phase_then_fails_at_the_repair_bound()
    {
        var current = Create(budget: Budget(maxRepairs: 1));
        var repair = AgentLoopStateMachine.Advance(
            current,
            Step(structured: false),
            Now.AddSeconds(1));

        Assert.True(repair.IsSuccess);
        Assert.Equal(AgentLoopPhase.Observe, repair.Value.Phase);
        Assert.Equal(1, repair.Value.StructuredRepairCount);

        var failed = AgentLoopStateMachine.Advance(
            repair.Value,
            Step(structured: false),
            Now.AddSeconds(2));

        Assert.True(failed.IsSuccess);
        Assert.Equal(AgentLoopState.Failed, failed.Value.State);
        Assert.Equal(FailureCode.ValidationFailure, failed.Value.FailureCode);
    }

    [Fact]
    public void Repeated_persist_evidence_stops_with_typed_no_progress()
    {
        var current = Create(budget: Budget(noProgress: 2));
        current = CompleteTurn(current, ProgressHash, Now);
        Assert.Equal(2, current.Turn);
        current = CompleteTurn(current, ProgressHash, Now.AddSeconds(10));
        Assert.Equal(3, current.Turn);
        current = CompleteTurn(current, ProgressHash, Now.AddSeconds(20));

        Assert.Equal(AgentLoopState.NoProgress, current.State);
        Assert.Equal(FailureCode.NoProgress, current.FailureCode);
        Assert.Equal(2, current.ConsecutiveNoProgress);
    }

    [Fact]
    public void Turn_token_tool_and_wall_limits_become_typed_budget_results()
    {
        var token = AgentLoopStateMachine.Advance(
            Create(budget: Budget(input: 5)),
            Step(inputTokens: 6),
            Now.AddSeconds(1));
        Assert.True(token.IsSuccess);
        Assert.Equal(AgentLoopState.BudgetExceeded, token.Value.State);

        var tool = AgentLoopStateMachine.Advance(
            Create(budget: Budget(tools: 0)),
            Step(toolCalls: 1),
            Now.AddSeconds(1));
        Assert.True(tool.IsSuccess);
        Assert.Equal(AgentLoopState.BudgetExceeded, tool.Value.State);

        var wall = AgentLoopStateMachine.Advance(
            Create(budget: Budget(wall: 1)),
            Step(),
            Now.AddSeconds(2));
        Assert.True(wall.IsSuccess);
        Assert.Equal(AgentLoopState.BudgetExceeded, wall.Value.State);

        var turn = CompleteTurn(Create(budget: Budget(turns: 1)), NextProgressHash, Now);
        Assert.Equal(AgentLoopState.BudgetExceeded, turn.State);
        Assert.Equal(FailureCode.BudgetExceeded, turn.FailureCode);
    }

    [Fact]
    public void Cancellation_is_terminal_and_snapshot_tampering_is_detected()
    {
        var current = Create();
        var canceled = AgentLoopStateMachine.Cancel(current, StepHash, Now.AddSeconds(1));

        Assert.True(canceled.IsSuccess);
        Assert.Equal(AgentLoopState.Canceled, canceled.Value.State);
        Assert.True(AgentLoopStateMachine.IsConsistent(canceled.Value));
        Assert.False(AgentLoopStateMachine.IsConsistent(canceled.Value with { Turn = 99 }));

        var repeat = AgentLoopStateMachine.Cancel(canceled.Value, StepHash, Now.AddSeconds(2));
        Assert.False(repeat.IsSuccess);
        Assert.Equal(FailureCode.ValidationFailure, repeat.Failure!.Code);
    }

    [Fact]
    public void Persist_requires_progress_evidence_and_malformed_authority_is_rejected()
    {
        var current = Create();
        for (var index = 0; index < 5; index++)
        {
            current = AgentLoopStateMachine.Advance(
                current,
                Step(),
                Now.AddSeconds(index + 1)).Value;
        }

        var missing = AgentLoopStateMachine.Advance(current, Step(), Now.AddSeconds(6));
        Assert.False(missing.IsSuccess);
        Assert.Equal(FailureCode.ValidationFailure, missing.Failure!.Code);

        var invalid = AgentLoopStateMachine.Create(
            new AgentLoopId(Guid.Empty),
            new InstallationId(Guid.NewGuid()),
            new AgentIdentityId(Guid.NewGuid()),
            1,
            Budget(),
            InitialHash,
            new ActorId("operator"),
            "loop-key",
            new CorrelationId("loop-correlation"),
            null,
            Now);
        Assert.False(invalid.IsSuccess);

        var prematureCompletion = AgentLoopStateMachine.Advance(
            Create(),
            Step(complete: true),
            Now.AddSeconds(1));
        Assert.False(prematureCompletion.IsSuccess);
        Assert.Equal(FailureCode.ValidationFailure, prematureCompletion.Failure?.Code);
    }

    private static AgentLoopSnapshot CompleteTurn(
        AgentLoopSnapshot current,
        string progress,
        DateTimeOffset baseTime)
    {
        for (var index = 0; index < 6; index++)
        {
            var result = AgentLoopStateMachine.Advance(
                current,
                Step(progress: index == 5 ? progress : null),
                baseTime.AddSeconds(current.Sequence + 1));
            Assert.True(result.IsSuccess, result.Failure?.Message);
            current = result.Value;
            if (AgentLoopStateMachine.IsTerminal(current.State))
            {
                break;
            }
        }

        return current;
    }

    private static AgentLoopSnapshot Create(AgentLoopBudget? budget = null)
    {
        var result = AgentLoopStateMachine.Create(
            new AgentLoopId(Guid.Parse("10000000-0000-0000-0000-000000000001")),
            new InstallationId(Guid.Parse("20000000-0000-0000-0000-000000000002")),
            new AgentIdentityId(Guid.Parse("30000000-0000-0000-0000-000000000003")),
            7,
            budget ?? Budget(),
            InitialHash,
            new ActorId("operator"),
            "loop-key",
            new CorrelationId("loop-correlation"),
            new CorrelationId("loop-cause"),
            Now);
        Assert.True(result.IsSuccess, result.Failure?.Message);
        return result.Value;
    }

    private static AgentLoopBudget Budget(
        int turns = 4,
        int tools = 4,
        long input = 1_000,
        long output = 1_000,
        int wall = 1_000,
        int maxRepairs = 2,
        int noProgress = 2) =>
        new(turns, tools, input, output, wall, maxRepairs, noProgress);

    private static AgentLoopStepResult Step(
        string? progress = null,
        long inputTokens = 0,
        long outputTokens = 0,
        int toolCalls = 0,
        bool structured = true,
        bool complete = false) =>
        new(StepHash, progress, inputTokens, outputTokens, toolCalls, structured, complete);
}
