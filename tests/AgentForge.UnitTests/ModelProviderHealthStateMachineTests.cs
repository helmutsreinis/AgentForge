using AgentForge.Domain.Installations;
using AgentForge.Domain.Models;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Providers;

namespace AgentForge.UnitTests;

public sealed class ModelProviderHealthStateMachineTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);
    private static readonly InstallationId InstallationId = new(Guid.Parse("742158b8-1cb1-4c86-aa91-d8f44d475ef0"));
    private static readonly ProviderProfileId ProfileId = new(Guid.Parse("e495a85a-19e3-4d24-919d-19ffb63e1a67"));
    private static readonly ModelRunId RunId = new(Guid.Parse("347ffb67-b9a8-4b89-8948-5e44a2577178"));
    private static readonly ModelRunAttemptId AttemptId = new(Guid.Parse("700e94fa-ad75-4772-969e-290a9d7173cc"));

    [Fact]
    public void Success_creates_healthy_observed_evidence()
    {
        var result = ModelProviderHealthStateMachine.Observe(null, Observation(
            ModelProviderHealthObservationOutcome.Succeeded));

        Assert.True(result.IsSuccess, result.Failure?.Message);
        Assert.True(result.Value.IsNew);
        Assert.Equal(ModelProviderHealthStatus.Healthy, result.Value.Record.Evidence.Status);
        Assert.Equal(ModelHealthEvidenceSource.Observed, result.Value.Record.Evidence.Source);
        Assert.Equal("attempt-succeeded", result.Value.Record.Evidence.EvidenceCode);
        Assert.Equal(0, result.Value.Record.Evidence.ConsecutiveFailures);
        Assert.Null(result.Value.Record.Evidence.RetryAfter);
        Assert.Equal(0, result.Value.Record.Version);
    }

    [Fact]
    public void Retryable_failures_increment_with_bounded_exponential_backoff()
    {
        ModelProviderHealthRecord? current = null;
        for (var index = 0; index < 8; index++)
        {
            var mutation = ModelProviderHealthStateMachine.Observe(
                current,
                Observation(ModelProviderHealthObservationOutcome.RetryableFailure, Now.AddMinutes(index)));
            Assert.True(mutation.IsSuccess, mutation.Failure?.Message);
            current = mutation.Value.Record;
        }

        Assert.Equal(ModelProviderHealthStatus.TemporarilyUnavailable, current!.Evidence.Status);
        Assert.Equal(8, current.Evidence.ConsecutiveFailures);
        Assert.Equal("attempt-retryable-failure", current.Evidence.EvidenceCode);
        Assert.Equal(current.Evidence.ObservedAt.AddMinutes(5), current.Evidence.RetryAfter);
        Assert.Equal(7, current.Version);
    }

    [Fact]
    public void Success_resets_prior_failure_evidence()
    {
        var failed = ModelProviderHealthStateMachine.Observe(
            null,
            Observation(ModelProviderHealthObservationOutcome.LeaseExpired)).Value.Record;

        var recovered = ModelProviderHealthStateMachine.Observe(
            failed,
            Observation(ModelProviderHealthObservationOutcome.Succeeded, Now.AddMinutes(1)));

        Assert.True(recovered.IsSuccess, recovered.Failure?.Message);
        Assert.Equal(0, recovered.Value.Record.Evidence.ConsecutiveFailures);
        Assert.Equal("attempt-succeeded", recovered.Value.Record.Evidence.EvidenceCode);
        Assert.Equal(1, recovered.Value.Record.Version);
    }

    [Fact]
    public void Observation_rejects_mismatched_authority_and_secret_shaped_metadata_is_bounded_upstream()
    {
        var current = ModelProviderHealthStateMachine.Observe(
            null,
            Observation(ModelProviderHealthObservationOutcome.Succeeded)).Value.Record;
        var mismatch = Observation(ModelProviderHealthObservationOutcome.Succeeded, Now.AddMinutes(1)) with
        {
            ProfileId = new ProviderProfileId(Guid.NewGuid()),
        };
        var invalid = Observation(ModelProviderHealthObservationOutcome.Succeeded) with
        {
            CorrelationId = new CorrelationId(new string('x', 129)),
        };

        Assert.Equal(
            FailureCode.ConcurrencyConflict,
            ModelProviderHealthStateMachine.Observe(current, mismatch).Failure?.Code);
        Assert.Equal(
            FailureCode.InvalidStateTransition,
            ModelProviderHealthStateMachine.Observe(null, invalid).Failure?.Code);
    }

    private static ModelProviderHealthObservation Observation(
        ModelProviderHealthObservationOutcome outcome,
        DateTimeOffset? observedAt = null) => new(
        InstallationId,
        ProfileId,
        RunId,
        AttemptId,
        outcome,
        new ActorId("model-worker"),
        new CorrelationId("health-test"),
        null,
        observedAt ?? Now);
}
