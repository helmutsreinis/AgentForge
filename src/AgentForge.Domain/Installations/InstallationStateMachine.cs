using AgentForge.Domain.Primitives;

namespace AgentForge.Domain.Installations;

public static class InstallationStateMachine
{
    public static DomainResult<InstallationSnapshot> Transition(
        InstallationSnapshot current,
        InstallationTrigger trigger,
        DateTimeOffset occurredAt,
        ActorId actorId,
        CorrelationId correlationId,
        string? reason = null)
    {
        ArgumentNullException.ThrowIfNull(current);

        var target = (current.State, trigger) switch
        {
            (InstallationState.Uninitialized, InstallationTrigger.BeginConfiguration) => InstallationState.Configuring,
            (InstallationState.Configuring, InstallationTrigger.BeginValidation) => InstallationState.Validating,
            (InstallationState.Validating, InstallationTrigger.ValidationSucceeded) => InstallationState.Ready,
            (InstallationState.Validating, InstallationTrigger.ValidationFailed) => InstallationState.RecoveryRequired,
            (InstallationState.Configuring, InstallationTrigger.ValidationFailed) => InstallationState.RecoveryRequired,
            (InstallationState.Ready, InstallationTrigger.StartRecovery) => InstallationState.RecoveryRequired,
            (InstallationState.RecoveryRequired, InstallationTrigger.ResumeConfiguration) => InstallationState.Configuring,
            (InstallationState.Configuring, InstallationTrigger.ConfigurationChanged) => InstallationState.Configuring,
            (InstallationState.Ready, InstallationTrigger.ConfigurationChanged) => InstallationState.Ready,
            _ => (InstallationState?)null,
        };

        if (target is null)
        {
            return DomainResult.Fail<InstallationSnapshot>(new DomainFailure(
                FailureCode.InvalidStateTransition,
                $"Trigger '{trigger}' is not valid while installation is '{current.State}'."));
        }

        if (target is InstallationState.RecoveryRequired && string.IsNullOrWhiteSpace(reason))
        {
            return DomainResult.Fail<InstallationSnapshot>(new DomainFailure(
                FailureCode.ValidationFailure,
                "A recovery reason is required when entering recovery mode."));
        }

        var recoveryReason = target is InstallationState.RecoveryRequired ? reason!.Trim() : null;
        var next = current with
        {
            State = target.Value,
            Version = checked(current.Version + 1),
            UpdatedAt = occurredAt,
            ActorId = actorId,
            CorrelationId = correlationId,
            RecoveryReason = recoveryReason,
        };

        return DomainResult.Success(next);
    }
}
