using AgentForge.Domain.Agents;
using AgentForge.Domain.Models;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Providers;

namespace AgentForge.UnitTests;

public sealed class ModelRunStateMachineTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 11, 10, 0, 0, TimeSpan.Zero);
    private static readonly string HashA = "sha256:" + new string('a', 64);
    private static readonly string HashB = "sha256:" + new string('b', 64);
    private static readonly string HashC = "sha256:" + new string('c', 64);
    private static readonly string HashD = "sha256:" + new string('d', 64);

    [Fact]
    public void Reservation_snapshots_route_and_budget_evidence()
    {
        var capabilities = new HashSet<ModelCapability> { ModelCapability.TextGeneration };
        var plan = CreatePlan(capabilities);

        var result = Reserve(plan);
        capabilities.Add(ModelCapability.ToolCalls);

        Assert.True(result.IsSuccess, result.Failure?.Message);
        Assert.Equal(ModelRunState.Reserved, result.Value.Run.State);
        Assert.Equal(ModelRunAttemptState.Planned, result.Value.Attempt.State);
        Assert.Equal(1, result.Value.Attempt.Sequence);
        Assert.Equal(4096, result.Value.Run.Reservation.OutputTokens);
        Assert.DoesNotContain(ModelCapability.ToolCalls, result.Value.Run.Route.RequiredCapabilities);
        Assert.Equal(plan.PlanEvidenceHash, result.Value.Attempt.PlanEvidenceHash);
    }

    [Fact]
    public void Reservation_rejects_expired_or_tampered_plans()
    {
        var plan = CreatePlan();

        var expired = Reserve(plan, plan.ValidUntil);
        var tampered = Reserve(plan with { PlanEvidenceHash = "not-a-hash" });

        Assert.Equal(FailureCode.InvalidStateTransition, expired.Failure?.Code);
        Assert.Equal(FailureCode.InvalidStateTransition, tampered.Failure?.Code);
    }

    [Fact]
    public void Reservation_rejects_overlong_plan_authority()
    {
        var plan = CreatePlan() with { ValidUntil = Now.AddSeconds(6) };

        var result = Reserve(plan);

        Assert.Equal(FailureCode.InvalidStateTransition, result.Failure?.Code);
    }

    [Fact]
    public void Started_run_completes_once_with_normalized_usage()
    {
        var started = Start();
        var usage = new ModelUsage(200, 100, 0, 0.25m, "usd");

        var completed = ModelRunStateMachine.Complete(
            started,
            usage,
            ModelFinishReason.Stop,
            Now.AddSeconds(2));

        Assert.True(completed.IsSuccess, completed.Failure?.Message);
        Assert.Equal(ModelRunState.Succeeded, completed.Value.Run.State);
        Assert.Equal(ModelRunAttemptState.Succeeded, completed.Value.Attempt.State);
        Assert.Equal("USD", completed.Value.Run.Usage.Currency);
        Assert.Equal(2, completed.Value.Run.Version);
        Assert.Equal(
            FailureCode.InvalidStateTransition,
            ModelRunStateMachine.Cancel(completed.Value, Now.AddSeconds(3)).Failure?.Code);
    }

    [Theory]
    [InlineData(1001, 0, 0, 1)]
    [InlineData(0, 4097, 0, 1)]
    [InlineData(0, 0, 5, 1)]
    [InlineData(0, 0, 0, 31)]
    public void Budget_exceeded_is_a_distinct_terminal_state(
        long inputTokens,
        long outputTokens,
        int toolCalls,
        int elapsedSeconds)
    {
        var started = Start();
        var usage = new ModelUsage(inputTokens, outputTokens, toolCalls, null, null);

        Assert.False(ModelRunStateMachine.Complete(
            started,
            usage,
            ModelFinishReason.Length,
            Now.AddSeconds(elapsedSeconds)).IsSuccess);

        var exceeded = ModelRunStateMachine.RecordBudgetExceeded(
            started,
            usage,
            Now.AddSeconds(elapsedSeconds));

        Assert.True(exceeded.IsSuccess, exceeded.Failure?.Message);
        Assert.Equal(ModelRunState.BudgetExceeded, exceeded.Value.Run.State);
        Assert.Equal(FailureCode.BudgetExceeded, exceeded.Value.Run.FailureCode);
    }

    [Fact]
    public void Failure_preserves_retry_classification_with_bounded_usage()
    {
        var failed = ModelRunStateMachine.Fail(
            Start(),
            new DomainFailure(FailureCode.RecoverableExternalFailure, "fixture", true),
            new ModelUsage(1, 2, 0, null, null),
            Now.AddSeconds(2));

        Assert.True(failed.IsSuccess, failed.Failure?.Message);
        Assert.Equal(ModelRunState.Failed, failed.Value.Run.State);
        Assert.Equal(ModelRunAttemptState.Failed, failed.Value.Attempt.State);
        Assert.True(failed.Value.Attempt.IsRetryable);
    }

    [Fact]
    public void Generic_failure_cannot_misclassify_budget_exhaustion()
    {
        var failed = ModelRunStateMachine.Fail(
            Start(),
            new DomainFailure(FailureCode.BudgetExceeded, "fixture"),
            new ModelUsage(1, 2, 0, null, null),
            Now.AddSeconds(2));

        Assert.Equal(FailureCode.InvalidStateTransition, failed.Failure?.Code);
    }

    [Fact]
    public void Reserved_and_running_runs_can_be_canceled_but_terminal_runs_cannot()
    {
        var reserved = Reserve(CreatePlan()).Value;
        var canceledReservation = ModelRunStateMachine.Cancel(reserved, Now.AddSeconds(1));
        var canceledRunning = ModelRunStateMachine.Cancel(Start(), Now.AddSeconds(1));

        Assert.Equal(ModelRunState.Canceled, canceledReservation.Value.Run.State);
        Assert.Equal(ModelRunAttemptState.Canceled, canceledRunning.Value.Attempt.State);
        Assert.False(ModelRunStateMachine.Start(canceledReservation.Value, Now.AddSeconds(2)).IsSuccess);
    }

    private static ModelRunAggregate Start()
    {
        var reserved = Reserve(CreatePlan()).Value;
        var started = ModelRunStateMachine.Start(reserved, Now);
        Assert.True(started.IsSuccess, started.Failure?.Message);
        return started.Value;
    }

    private static DomainResult<ModelRunAggregate> Reserve(
        ModelRoutePlan plan,
        DateTimeOffset? at = null) =>
        ModelRunStateMachine.Reserve(
            new ModelRunId(Guid.Parse("fcfd1394-b273-46d7-b713-3582361f1d76")),
            new ModelRunAttemptId(Guid.Parse("fc01dd06-8104-43d1-998b-2df9f65d5ac1")),
            plan,
            new ActorId("worker"),
            HashD,
            "model-run-001",
            new CorrelationId("model-run"),
            null,
            at ?? Now);

    private static ModelRoutePlan CreatePlan(IReadOnlySet<ModelCapability>? capabilities = null) => new(
        new ModelRequestId(Guid.Parse("4662178b-31aa-44fc-9268-937268e58a97")),
        new InstallationId(Guid.Parse("21821590-9d3f-4c8a-b258-a5215cb5e6ad")),
        7,
        new AgentIdentityId(Guid.Parse("23e09ddb-1c4c-449c-984e-c1fe8df4cce9")),
        3,
        4,
        new ModelRouteSelection(
            new ProviderProfileId(Guid.Parse("e95a077c-e068-472c-9581-70391aeb03d1")),
            "fake",
            "fixture-model",
            false,
            capabilities ?? new HashSet<ModelCapability> { ModelCapability.TextGeneration },
            HashA),
        HashB,
        2,
        "redact-v1",
        HashC,
        1000,
        4096,
        4,
        30,
        Now,
        Now.AddSeconds(5),
        HashD);
}
