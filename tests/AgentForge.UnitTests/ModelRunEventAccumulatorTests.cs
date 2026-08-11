using AgentForge.Domain.Agents;
using AgentForge.Domain.Models;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Providers;
using AgentForge.Models;

namespace AgentForge.UnitTests;

public sealed class ModelRunEventAccumulatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 11, 15, 0, 0, TimeSpan.Zero);
    private static readonly string HashA = "sha256:" + new string('a', 64);
    private static readonly string HashB = "sha256:" + new string('b', 64);

    [Fact]
    public void Valid_stream_produces_contiguous_hash_evidence_and_normalized_usage()
    {
        var run = CreateRun();
        using var accumulator = new ModelRunEventAccumulator(run);

        Assert.True(accumulator.Accept(Started(run, 0)).IsSuccess);
        Assert.True(accumulator.Accept(new ModelTextDeltaEvent(run.RequestId, 1, Now, "hello")).IsSuccess);
        Assert.True(accumulator.Accept(new ModelUsageEvent(
            run.RequestId,
            2,
            Now,
            new ModelUsage(12, 3, 0, 0.02m, "usd"))).IsSuccess);
        Assert.True(accumulator.Accept(new ModelCompletedEvent(
            run.RequestId,
            3,
            Now,
            ModelFinishReason.Stop)).IsSuccess);

        var evidence = accumulator.CompleteEvidence();

        Assert.True(evidence.IsSuccess, evidence.Failure?.Message);
        Assert.Equal(4, evidence.Value.EventCount);
        Assert.Equal(3, evidence.Value.LastSequence);
        Assert.StartsWith("sha256:", evidence.Value.EventStreamHash, StringComparison.Ordinal);
        Assert.Equal("USD", accumulator.Usage.Currency);
        Assert.Equal(ModelFinishReason.Stop, accumulator.FinishReason);
    }

    [Fact]
    public void Stream_rejects_wrong_identity_sequence_route_and_post_terminal_events()
    {
        var run = CreateRun();
        using var wrongRoute = new ModelRunEventAccumulator(run);
        var badStart = Started(run, 0) with { InputHash = HashA };
        Assert.Equal(FailureCode.RecoverableExternalFailure, wrongRoute.Accept(badStart).Failure?.Code);

        using var wrongSequence = new ModelRunEventAccumulator(run);
        Assert.Equal(
            FailureCode.RecoverableExternalFailure,
            wrongSequence.Accept(Started(run, 1)).Failure?.Code);

        using var terminal = new ModelRunEventAccumulator(run);
        Assert.True(terminal.Accept(Started(run, 0)).IsSuccess);
        Assert.True(terminal.Accept(new ModelCompletedEvent(
            run.RequestId,
            1,
            Now,
            ModelFinishReason.Stop)).IsSuccess);
        Assert.Equal(
            FailureCode.RecoverableExternalFailure,
            terminal.Accept(new ModelTextDeltaEvent(run.RequestId, 2, Now, "late")).Failure?.Code);
    }

    [Fact]
    public void Tool_events_are_denied_and_event_overflow_is_preserved_as_evidence()
    {
        var run = CreateRun() with
        {
            Reservation = new ModelRunBudgetReservation(1000, 4096, 0, 2, 30),
        };
        using var tools = new ModelRunEventAccumulator(run);
        Assert.True(tools.Accept(Started(run, 0)).IsSuccess);
        var tool = tools.Accept(new ModelToolCallDeltaEvent(
            run.RequestId,
            1,
            Now,
            "call-1",
            "read_file",
            "{}"));
        Assert.Equal(FailureCode.UnsupportedCapability, tool.Failure?.Code);

        using var overflow = new ModelRunEventAccumulator(run);
        Assert.True(overflow.Accept(Started(run, 0)).IsSuccess);
        Assert.True(overflow.Accept(new ModelTextDeltaEvent(run.RequestId, 1, Now, "one")).IsSuccess);
        var exceeded = overflow.Accept(new ModelTextDeltaEvent(run.RequestId, 2, Now, "two"));
        Assert.Equal(FailureCode.BudgetExceeded, exceeded.Failure?.Code);
        var evidence = overflow.FinalizeEvidence();
        Assert.Equal(3, evidence.EventCount);
        Assert.Equal(2, evidence.LastSequence);
    }

    [Fact]
    public void Unterminated_stream_returns_retryable_failure_without_losing_accepted_evidence()
    {
        var run = CreateRun();
        using var accumulator = new ModelRunEventAccumulator(run);
        Assert.True(accumulator.Accept(Started(run, 0)).IsSuccess);

        var completed = accumulator.CompleteEvidence();
        var evidence = accumulator.FinalizeEvidence();

        Assert.Equal(FailureCode.RecoverableExternalFailure, completed.Failure?.Code);
        Assert.True(completed.Failure?.IsRetryable);
        Assert.Equal(1, evidence.EventCount);
        Assert.Equal(0, evidence.LastSequence);
    }

    [Fact]
    public void Maximum_reservation_overflow_remains_valid_terminal_budget_evidence()
    {
        var run = CreateRun() with
        {
            Reservation = new ModelRunBudgetReservation(1000, 4096, 0, 100_000, 30),
        };
        using var accumulator = new ModelRunEventAccumulator(run);
        Assert.True(accumulator.Accept(Started(run, 0)).IsSuccess);
        for (var sequence = 1; sequence <= 100_000; sequence++)
        {
            var result = accumulator.Accept(new ModelTextDeltaEvent(
                run.RequestId,
                sequence,
                Now,
                string.Empty));
            if (sequence < 100_000)
            {
                Assert.True(result.IsSuccess, result.Failure?.Message);
            }
            else
            {
                Assert.Equal(FailureCode.BudgetExceeded, result.Failure?.Code);
            }
        }

        var evidence = accumulator.FinalizeEvidence();
        Assert.Equal(100_001, evidence.EventCount);
        var started = ModelRunStateMachine.Start(
            new ModelRunAggregate(
                run,
                ModelRunStateMachine.Reserve(
                    new ModelRunId(Guid.Parse("ef589f32-090a-4a9f-97fd-86396873fda5")),
                    new ModelRunAttemptId(Guid.Parse("c1916a94-1e39-4366-a47f-a3d00ec3d8dc")),
                    CreatePlanFor(run),
                    new ActorId("worker"),
                    HashA,
                    "model-run-event-accumulator",
                    new CorrelationId("model-run"),
                    null,
                    Now).Value.Attempt),
            "worker",
            new string('L', 43),
            Now,
            Now.AddSeconds(45)).Value;
        var terminal = ModelRunStateMachine.RecordBudgetExceeded(
            started,
            new string('L', 43),
            new ModelUsage(0, 0, 0, null, null),
            evidence,
            Now.AddSeconds(1));
        Assert.True(terminal.IsSuccess, terminal.Failure?.Message);
    }

    private static ModelStartedEvent Started(ModelRunRecord run, long sequence) => new(
        run.RequestId,
        sequence,
        Now,
        run.Route.ProfileId,
        run.Route.ProviderType,
        run.Route.Model,
        run.PreparedInputHash,
        HashA);

    private static ModelRunRecord CreateRun()
    {
        var requestId = new ModelRequestId(Guid.Parse("4d03a1ce-cf18-4f35-bcea-d849911fc86d"));
        var plan = new ModelRoutePlan(
            requestId,
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
                HashA),
            HashB,
            0,
            "redact-v1",
            HashA,
            1000,
            4096,
            0,
            32,
            30,
            Now,
            Now.AddSeconds(5),
            HashA);
        return ModelRunStateMachine.Reserve(
            new ModelRunId(Guid.Parse("ef589f32-090a-4a9f-97fd-86396873fda5")),
            new ModelRunAttemptId(Guid.Parse("c1916a94-1e39-4366-a47f-a3d00ec3d8dc")),
            plan,
            new ActorId("worker"),
            HashA,
            "model-run-event-accumulator",
            new CorrelationId("model-run"),
            null,
            Now).Value.Run;
    }

    private static ModelRoutePlan CreatePlanFor(ModelRunRecord run) => new(
        run.RequestId,
        run.InstallationId,
        run.InstallationVersion,
        run.AgentId,
        run.AgentVersion,
        run.ProviderVersion,
        run.AttemptedProfileIds,
        run.Route,
        run.PreparedInputHash,
        run.ContextRedactionCount,
        run.ContextPreparationPolicy,
        run.HealthEvidenceHash,
        run.Reservation.InputTokens,
        run.Reservation.OutputTokens,
        run.Reservation.ToolCalls,
        run.Reservation.Events,
        run.Reservation.WallClockSeconds,
        Now,
        Now.AddSeconds(5),
        run.PlanEvidenceHash);
}
