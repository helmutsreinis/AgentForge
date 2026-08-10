namespace AgentForge.Domain.Installations;

public enum InstallationState
{
    Uninitialized,
    Configuring,
    Validating,
    Ready,
    RecoveryRequired,
}

public enum InstallationTrigger
{
    BeginConfiguration,
    BeginValidation,
    ValidationSucceeded,
    ValidationFailed,
    StartRecovery,
    ResumeConfiguration,
}
