using AgentForge.Domain.Installations;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Providers;

namespace AgentForge.Domain.Models;

public enum ModelProviderHealthObservationOutcome
{
    Succeeded,
    RetryableFailure,
    LeaseExpired,
}

public sealed record ModelProviderHealthObservation(
    InstallationId InstallationId,
    ProviderProfileId ProfileId,
    ModelRunId RunId,
    ModelRunAttemptId AttemptId,
    ModelProviderHealthObservationOutcome Outcome,
    ActorId ActorId,
    CorrelationId CorrelationId,
    CorrelationId? CausationId,
    DateTimeOffset ObservedAt);

public sealed record ModelProviderHealthRecord(
    InstallationId InstallationId,
    ModelProviderHealthEvidence Evidence,
    ModelRunId LastRunId,
    ModelRunAttemptId LastAttemptId,
    ActorId ActorId,
    CorrelationId CorrelationId,
    CorrelationId? CausationId,
    DateTimeOffset UpdatedAt,
    long Version);

public sealed record ModelProviderHealthMutation(
    ModelProviderHealthRecord Record,
    bool IsNew,
    long? ExpectedVersion);

public static class ModelProviderHealthStateMachine
{
    private static readonly TimeSpan HealthyLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan FailureLifetime = TimeSpan.FromMinutes(15);

    public static DomainResult<ModelProviderHealthMutation> Observe(
        ModelProviderHealthRecord? current,
        ModelProviderHealthObservation observation)
    {
        if (!ValidateObservation(observation) || current is not null && !ValidateRecord(current))
        {
            return Invalid("Model provider health observation evidence is invalid or unbounded.");
        }

        if (current is not null &&
            (current.InstallationId != observation.InstallationId ||
                current.Evidence.ProfileId != observation.ProfileId))
        {
            return Conflict("Model provider health authority does not match the observed profile.");
        }

        if (current is not null && observation.ObservedAt <= current.UpdatedAt)
        {
            return Conflict("Model provider health observations must advance durable evidence time.");
        }

        var isNew = current is null;
        var previousFailures = current?.Evidence.ConsecutiveFailures ?? 0;
        ModelProviderHealthEvidence evidence;
        if (observation.Outcome is ModelProviderHealthObservationOutcome.Succeeded)
        {
            evidence = new ModelProviderHealthEvidence(
                observation.ProfileId,
                ModelProviderHealthStatus.Healthy,
                ModelHealthEvidenceSource.Observed,
                0,
                "attempt-succeeded",
                observation.ObservedAt,
                observation.ObservedAt.Add(HealthyLifetime));
        }
        else
        {
            var failures = Math.Min(1_000, checked(previousFailures + 1));
            var exponent = Math.Min(failures - 1, 6);
            var backoffSeconds = Math.Min(300, 5 * (1 << exponent));
            evidence = new ModelProviderHealthEvidence(
                observation.ProfileId,
                ModelProviderHealthStatus.TemporarilyUnavailable,
                ModelHealthEvidenceSource.Observed,
                failures,
                observation.Outcome is ModelProviderHealthObservationOutcome.LeaseExpired
                    ? "lease-expired"
                    : "attempt-retryable-failure",
                observation.ObservedAt,
                observation.ObservedAt.Add(FailureLifetime),
                observation.ObservedAt.AddSeconds(backoffSeconds));
        }

        var next = new ModelProviderHealthRecord(
            observation.InstallationId,
            evidence,
            observation.RunId,
            observation.AttemptId,
            observation.ActorId,
            observation.CorrelationId,
            observation.CausationId,
            observation.ObservedAt,
            isNew ? 0 : checked(current!.Version + 1));
        return DomainResult.Success(new ModelProviderHealthMutation(
            next,
            isNew,
            isNew ? null : current!.Version));
    }

    private static bool ValidateObservation(ModelProviderHealthObservation observation) =>
        observation is not null && observation.InstallationId.Value != Guid.Empty &&
        observation.ProfileId.Value != Guid.Empty && observation.RunId.Value != Guid.Empty &&
        observation.AttemptId.Value != Guid.Empty && Enum.IsDefined(observation.Outcome) &&
        IsBounded(observation.ActorId.Value, 256) &&
        IsBounded(observation.CorrelationId.Value, 128) &&
        (observation.CausationId is null || IsBounded(observation.CausationId.Value.Value, 128)) &&
        observation.ObservedAt != default;

    private static bool ValidateRecord(ModelProviderHealthRecord record) =>
        record is not null && record.InstallationId.Value != Guid.Empty && record.Evidence is not null &&
        record.Evidence.ProfileId.Value != Guid.Empty && record.LastRunId.Value != Guid.Empty &&
        record.LastAttemptId.Value != Guid.Empty && record.Version >= 0 && record.UpdatedAt != default &&
        record.Evidence.ObservedAt == record.UpdatedAt &&
        Enum.IsDefined(record.Evidence.Status) &&
        record.Evidence.Source is ModelHealthEvidenceSource.Observed &&
        record.Evidence.ConsecutiveFailures is >= 0 and <= 1_000 &&
        record.Evidence.ExpiresAt > record.Evidence.ObservedAt &&
        record.Evidence.ExpiresAt - record.Evidence.ObservedAt <= FailureLifetime &&
        (record.Evidence.Status is ModelProviderHealthStatus.Healthy
            ? record.Evidence.ConsecutiveFailures == 0 && record.Evidence.RetryAfter is null &&
                record.Evidence.EvidenceCode == "attempt-succeeded" &&
                record.Evidence.ExpiresAt - record.Evidence.ObservedAt == HealthyLifetime
            : record.Evidence.Status is ModelProviderHealthStatus.TemporarilyUnavailable &&
                record.Evidence.ConsecutiveFailures > 0 &&
                record.Evidence.RetryAfter > record.Evidence.ObservedAt &&
                record.Evidence.RetryAfter <= record.Evidence.ExpiresAt &&
                record.Evidence.EvidenceCode is "attempt-retryable-failure" or "lease-expired") &&
        IsBounded(record.ActorId.Value, 256) &&
        IsBounded(record.CorrelationId.Value, 128) &&
        (record.CausationId is null || IsBounded(record.CausationId.Value.Value, 128));

    private static bool IsBounded(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength && !value.Any(char.IsControl);

    private static DomainResult<ModelProviderHealthMutation> Invalid(string message) =>
        DomainResult.Fail<ModelProviderHealthMutation>(new DomainFailure(
            FailureCode.InvalidStateTransition,
            message));

    private static DomainResult<ModelProviderHealthMutation> Conflict(string message) =>
        DomainResult.Fail<ModelProviderHealthMutation>(new DomainFailure(
            FailureCode.ConcurrencyConflict,
            message,
            true));
}
