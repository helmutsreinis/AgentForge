using AgentForge.Domain.Agents;
using AgentForge.Domain.Models;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Providers;

namespace AgentForge.UnitTests;

public sealed class ModelBudgetLedgerStateMachineTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 11, 14, 0, 0, TimeSpan.Zero);
    private static readonly string Hash = "sha256:" + new string('a', 64);
    private static readonly string LeaseToken = new('L', 43);
    private static readonly AgentBudget Budget = new(8, 4, 1500, 5000, 60);

    [Fact]
    public void Reservation_creates_an_exact_versioned_shared_ledger()
    {
        var run = CreateReservedRun();

        var result = ModelBudgetLedgerStateMachine.Reserve(null, run.Run, Budget, Now);

        Assert.True(result.IsSuccess, result.Failure?.Message);
        Assert.True(result.Value.IsNew);
        Assert.Null(result.Value.ExpectedVersion);
        Assert.Equal(0, result.Value.Ledger.Version);
        Assert.Equal(1, result.Value.Ledger.ActiveRuns);
        Assert.Equal(run.Run.Reservation, result.Value.Ledger.ActiveReservation);
        Assert.Equal(0, result.Value.Ledger.Consumption.CompletedRuns);
    }

    [Fact]
    public void Concurrent_reservations_cannot_overcommit_agent_budget()
    {
        var first = CreateReservedRun();
        var second = CreateReservedRun(Guid.Parse("83055333-c129-401b-b5db-dbe34aa02795"));
        var ledger = ModelBudgetLedgerStateMachine.Reserve(null, first.Run, Budget, Now).Value.Ledger;

        var result = ModelBudgetLedgerStateMachine.Reserve(ledger, second.Run, Budget, Now.AddSeconds(1));

        Assert.Equal(FailureCode.BudgetExceeded, result.Failure?.Code);
        Assert.Equal(1, ledger.ActiveRuns);
        Assert.Equal(first.Run.Reservation, ledger.ActiveReservation);
    }

    [Fact]
    public void Reservation_rejects_a_changed_agent_version()
    {
        var run = CreateReservedRun();
        var ledger = ModelBudgetLedgerStateMachine.Reserve(null, run.Run, Budget, Now).Value.Ledger;
        var changed = run.Run with { AgentVersion = run.Run.AgentVersion + 1 };

        var result = ModelBudgetLedgerStateMachine.Reserve(ledger, changed, Budget, Now.AddSeconds(1));

        Assert.Equal(FailureCode.ConcurrencyConflict, result.Failure?.Code);
    }

    [Fact]
    public void Idle_ledger_rolls_forward_to_a_new_agent_version_without_losing_consumption()
    {
        var run = CreateReservedRun();
        var idle = new ModelBudgetLedgerRecord(
            run.Run.InstallationId,
            run.Run.AgentId,
            run.Run.AgentVersion - 1,
            new ModelRunBudgetReservation(0, 0, 0, 0, 0),
            0,
            new ModelBudgetConsumption(10, 20, 0, 4, 1, 1),
            Now.AddSeconds(-1),
            5);

        var result = ModelBudgetLedgerStateMachine.Reserve(idle, run.Run, Budget, Now);

        Assert.True(result.IsSuccess, result.Failure?.Message);
        Assert.Equal(run.Run.AgentVersion, result.Value.Ledger.AgentVersion);
        Assert.Equal(6, result.Value.Ledger.Version);
        Assert.Equal(idle.Consumption, result.Value.Ledger.Consumption);
    }

    [Fact]
    public void Terminal_reconciliation_releases_reservation_and_accumulates_evidence_once()
    {
        var reserved = CreateReservedRun();
        var ledger = ModelBudgetLedgerStateMachine.Reserve(null, reserved.Run, Budget, Now).Value.Ledger;
        var started = ModelRunStateMachine.Start(
            reserved,
            "worker-01",
            LeaseToken,
            Now,
            Now.AddSeconds(45)).Value;
        var terminal = ModelRunStateMachine.Complete(
            started,
            LeaseToken,
            new ModelUsage(100, 75, 1, 0.1m, "usd"),
            new ModelRunStreamEvidence(4, 3, Hash),
            ModelFinishReason.Stop,
            Now.AddMilliseconds(1500)).Value;

        var result = ModelBudgetLedgerStateMachine.Reconcile(ledger, terminal.Run, Now.AddSeconds(2));

        Assert.True(result.IsSuccess, result.Failure?.Message);
        Assert.False(result.Value.IsNew);
        Assert.Equal(0, result.Value.ExpectedVersion);
        Assert.Equal(1, result.Value.Ledger.Version);
        Assert.Equal(0, result.Value.Ledger.ActiveRuns);
        Assert.Equal(new ModelRunBudgetReservation(0, 0, 0, 0, 0), result.Value.Ledger.ActiveReservation);
        Assert.Equal(new ModelBudgetConsumption(100, 75, 1, 4, 2, 1), result.Value.Ledger.Consumption);

        var duplicate = ModelBudgetLedgerStateMachine.Reconcile(
            result.Value.Ledger,
            terminal.Run,
            Now.AddSeconds(3));
        Assert.Equal(FailureCode.InvalidStateTransition, duplicate.Failure?.Code);
    }

    private static ModelRunAggregate CreateReservedRun(Guid? runId = null)
    {
        var id = runId ?? Guid.Parse("4f04abf6-b115-4d6f-a82b-9bb04f0d18c3");
        var plan = new ModelRoutePlan(
            new ModelRequestId(id),
            new InstallationId(Guid.Parse("21821590-9d3f-4c8a-b258-a5215cb5e6ad")),
            7,
            new AgentIdentityId(Guid.Parse("23e09ddb-1c4c-449c-984e-c1fe8df4cce9")),
            3,
            4,
            [],
            new ModelRouteSelection(
                new ProviderProfileId(Guid.Parse("e95a077c-e068-472c-9581-70391aeb03d1")),
                "fake",
                "fixture-model",
                false,
                new HashSet<ModelCapability> { ModelCapability.TextGeneration },
                Hash),
            Hash,
            0,
            "redact-v1",
            Hash,
            1000,
            4096,
            4,
            32,
            30,
            Now,
            Now.AddSeconds(5),
            Hash);
        return ModelRunStateMachine.Reserve(
            new ModelRunId(id),
            new ModelRunAttemptId(Guid.NewGuid()),
            plan,
            new ActorId("worker"),
            Hash,
            $"model-run-{id:D}",
            new CorrelationId("model-run"),
            null,
            Now).Value;
    }
}
