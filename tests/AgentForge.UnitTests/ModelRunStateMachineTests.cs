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
    private static readonly string LeaseToken = new('L', 43);
    private static readonly ModelRunStreamEvidence StreamEvidence = new(2, 1, HashA);

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
        Assert.Equal(32, result.Value.Run.Reservation.Events);
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
            LeaseToken,
            usage,
            StreamEvidence,
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
    [InlineData(0, 0, 0, 30)]
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
            LeaseToken,
            usage,
            StreamEvidence,
            ModelFinishReason.Length,
            Now.AddSeconds(elapsedSeconds)).IsSuccess);

        var exceeded = ModelRunStateMachine.RecordBudgetExceeded(
            started,
            LeaseToken,
            usage,
            StreamEvidence,
            Now.AddSeconds(elapsedSeconds));

        Assert.True(exceeded.IsSuccess, exceeded.Failure?.Message);
        Assert.Equal(ModelRunState.BudgetExceeded, exceeded.Value.Run.State);
        Assert.Equal(FailureCode.BudgetExceeded, exceeded.Value.Run.FailureCode);
    }

    [Fact]
    public void Event_overflow_cannot_be_recorded_as_success()
    {
        var started = Start();
        var overflow = new ModelRunStreamEvidence(33, 32, HashA);
        var usage = new ModelUsage(1, 2, 0, null, null);

        var completed = ModelRunStateMachine.Complete(
            started,
            LeaseToken,
            usage,
            overflow,
            ModelFinishReason.Stop,
            Now.AddSeconds(1));
        var exceeded = ModelRunStateMachine.RecordBudgetExceeded(
            started,
            LeaseToken,
            usage,
            overflow,
            Now.AddSeconds(1));

        Assert.Equal(FailureCode.InvalidStateTransition, completed.Failure?.Code);
        Assert.True(exceeded.IsSuccess, exceeded.Failure?.Message);
        Assert.Equal(ModelRunState.BudgetExceeded, exceeded.Value.Run.State);
    }

    [Fact]
    public void Failure_preserves_retry_classification_with_bounded_usage()
    {
        var failed = ModelRunStateMachine.Fail(
            Start(),
            LeaseToken,
            new DomainFailure(FailureCode.RecoverableExternalFailure, "fixture", true),
            new ModelUsage(1, 2, 0, null, null),
            StreamEvidence,
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
            LeaseToken,
            new DomainFailure(FailureCode.BudgetExceeded, "fixture"),
            new ModelUsage(1, 2, 0, null, null),
            StreamEvidence,
            Now.AddSeconds(2));

        Assert.Equal(FailureCode.InvalidStateTransition, failed.Failure?.Code);
    }

    [Fact]
    public void Terminal_transition_requires_the_exact_unpersisted_lease_token()
    {
        var started = Start();

        var result = ModelRunStateMachine.Complete(
            started,
            new string('X', 43),
            new ModelUsage(1, 2, 0, null, null),
            StreamEvidence,
            ModelFinishReason.Stop,
            Now.AddSeconds(2));

        Assert.Equal(FailureCode.InvalidStateTransition, result.Failure?.Code);
        Assert.NotEqual(LeaseToken, started.Run.Lease?.TokenHash);
        Assert.StartsWith("sha256:", started.Run.Lease?.TokenHash, StringComparison.Ordinal);
    }

    [Fact]
    public void Start_rejects_oversized_or_malformed_leases()
    {
        var reserved = Reserve(CreatePlan()).Value;

        var oversized = ModelRunStateMachine.Start(
            reserved,
            "model-worker",
            LeaseToken,
            Now,
            Now.AddSeconds(91));
        var malformed = ModelRunStateMachine.Start(
            reserved,
            "model-worker",
            new string('!', 43),
            Now,
            Now.AddSeconds(45));

        Assert.Equal(FailureCode.InvalidStateTransition, oversized.Failure?.Code);
        Assert.Equal(FailureCode.InvalidStateTransition, malformed.Failure?.Code);
    }

    [Fact]
    public void Expired_lease_cannot_authorize_a_terminal_transition()
    {
        var reserved = Reserve(CreatePlan()).Value;
        var started = ModelRunStateMachine.Start(
            reserved,
            "model-worker",
            LeaseToken,
            Now,
            Now.AddSeconds(1)).Value;

        var result = ModelRunStateMachine.Complete(
            started,
            LeaseToken,
            new ModelUsage(1, 2, 0, null, null),
            StreamEvidence,
            ModelFinishReason.Stop,
            Now.AddSeconds(2));

        Assert.Equal(FailureCode.InvalidStateTransition, result.Failure?.Code);
    }

    [Fact]
    public void Exact_lease_holder_can_advance_a_monotonic_heartbeat_without_extending_expiry()
    {
        var started = Start();

        var heartbeat = ModelRunStateMachine.Heartbeat(
            started,
            "model-worker",
            LeaseToken,
            Now.AddSeconds(10));

        Assert.True(heartbeat.IsSuccess, heartbeat.Failure?.Message);
        Assert.Equal(Now.AddSeconds(10), heartbeat.Value.Run.Lease?.HeartbeatAt);
        Assert.Equal(started.Run.Lease?.ExpiresAt, heartbeat.Value.Run.Lease?.ExpiresAt);
        Assert.Equal(2, heartbeat.Value.Run.Version);
        Assert.Equal(1, heartbeat.Value.Attempt.Version);
        Assert.Same(started.Attempt, heartbeat.Value.Attempt);
    }

    [Theory]
    [InlineData("other-worker", "LLLLLLLLLLLLLLLLLLLLLLLLLLLLLLLLLLLLLLLLLLL", 10)]
    [InlineData("model-worker", "XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX", 10)]
    [InlineData("model-worker", "LLLLLLLLLLLLLLLLLLLLLLLLLLLLLLLLLLLLLLLLLLL", 0)]
    [InlineData("model-worker", "LLLLLLLLLLLLLLLLLLLLLLLLLLLLLLLLLLLLLLLLLLL", 46)]
    public void Heartbeat_rejects_wrong_or_non_monotonic_lease_evidence(
        string owner,
        string token,
        int seconds)
    {
        var result = ModelRunStateMachine.Heartbeat(Start(), owner, token, Now.AddSeconds(seconds));

        Assert.Equal(FailureCode.InvalidStateTransition, result.Failure?.Code);
    }

    [Fact]
    public void Expired_lease_recovery_is_retryable_and_uses_the_persisted_expiry_boundary()
    {
        var started = ModelRunStateMachine.Start(
            Reserve(CreatePlan()).Value,
            "model-worker",
            LeaseToken,
            Now,
            Now.AddSeconds(5)).Value;

        var early = ModelRunStateMachine.RecoverExpiredLease(started, Now.AddSeconds(4));
        var recovered = ModelRunStateMachine.RecoverExpiredLease(started, Now.AddSeconds(20));

        Assert.Equal(FailureCode.InvalidStateTransition, early.Failure?.Code);
        Assert.True(recovered.IsSuccess, recovered.Failure?.Message);
        Assert.Equal(ModelRunState.Failed, recovered.Value.Run.State);
        Assert.Equal(ModelRunAttemptState.Failed, recovered.Value.Attempt.State);
        Assert.Equal(FailureCode.RecoverableExternalFailure, recovered.Value.Run.FailureCode);
        Assert.True(recovered.Value.Attempt.IsRetryable);
        Assert.Equal(Now.AddSeconds(5), recovered.Value.Run.CompletedAt);
        Assert.Equal(ModelRunStreamEvidence.Empty, recovered.Value.Run.StreamEvidence);
    }

    [Fact]
    public void Reserved_and_running_runs_can_be_canceled_but_terminal_runs_cannot()
    {
        var reserved = Reserve(CreatePlan()).Value;
        var canceledReservation = ModelRunStateMachine.Cancel(reserved, Now.AddSeconds(1));
        var canceledRunning = ModelRunStateMachine.CancelRunning(
            Start(),
            LeaseToken,
            new ModelUsage(0, 0, 0, null, null),
            StreamEvidence,
            Now.AddSeconds(1));

        Assert.Equal(ModelRunState.Canceled, canceledReservation.Value.Run.State);
        Assert.Equal(ModelRunAttemptState.Canceled, canceledRunning.Value.Attempt.State);
        Assert.False(ModelRunStateMachine.Start(
            canceledReservation.Value,
            "model-worker",
            LeaseToken,
            Now.AddSeconds(2),
            Now.AddSeconds(10)).IsSuccess);
    }

    private static ModelRunAggregate Start()
    {
        var reserved = Reserve(CreatePlan()).Value;
        var started = ModelRunStateMachine.Start(
            reserved,
            "model-worker",
            LeaseToken,
            Now,
            Now.AddSeconds(45));
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
        [],
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
        32,
        30,
        Now,
        Now.AddSeconds(5),
        HashD);
}
