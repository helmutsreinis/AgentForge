using AgentForge.Domain.Installations;
using AgentForge.Domain.Primitives;

namespace AgentForge.UnitTests;

public sealed class InstallationStateMachineTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly ActorId Actor = new("operator-1");
    private static readonly CorrelationId Correlation = new("test-correlation");

    [Fact]
    public void Valid_setup_path_reaches_ready_with_monotonic_versions()
    {
        var initial = InstallationSnapshot.CreateUninitialized(
            new InstallationId(Guid.Parse("1b00dcc9-9b33-4c8a-b37a-d6a0e2c6fb74")),
            Now,
            Actor,
            Correlation);

        var configuring = Transition(initial, InstallationTrigger.BeginConfiguration);
        var validating = Transition(configuring, InstallationTrigger.BeginValidation);
        var ready = Transition(validating, InstallationTrigger.ValidationSucceeded);

        Assert.Equal(InstallationState.Ready, ready.State);
        Assert.Equal(3, ready.Version);
        Assert.True(ready.IsReady);
        Assert.Null(ready.RecoveryReason);
    }

    [Fact]
    public void Invalid_transition_fails_without_changing_the_snapshot()
    {
        var initial = InstallationSnapshot.CreateUninitialized(
            InstallationId.New(),
            Now,
            Actor,
            Correlation);

        var result = InstallationStateMachine.Transition(
            initial,
            InstallationTrigger.ValidationSucceeded,
            Now.AddMinutes(1),
            Actor,
            Correlation);

        Assert.False(result.IsSuccess);
        Assert.Equal(FailureCode.InvalidStateTransition, result.Failure?.Code);
        Assert.Equal(InstallationState.Uninitialized, initial.State);
        Assert.Equal(0, initial.Version);
    }

    [Fact]
    public void Entering_recovery_requires_an_attributed_reason()
    {
        var ready = InstallationSnapshot.CreateUninitialized(InstallationId.New(), Now, Actor, Correlation) with
        {
            State = InstallationState.Ready,
            Version = 3,
        };

        var result = InstallationStateMachine.Transition(
            ready,
            InstallationTrigger.StartRecovery,
            Now.AddMinutes(1),
            Actor,
            Correlation);

        Assert.False(result.IsSuccess);
        Assert.Equal(FailureCode.ValidationFailure, result.Failure?.Code);
    }

    private static InstallationSnapshot Transition(
        InstallationSnapshot current,
        InstallationTrigger trigger)
    {
        var result = InstallationStateMachine.Transition(
            current,
            trigger,
            current.UpdatedAt.AddMinutes(1),
            Actor,
            Correlation,
            trigger is InstallationTrigger.ValidationFailed or InstallationTrigger.StartRecovery ? "test failure" : null);

        Assert.True(result.IsSuccess, result.Failure?.Message);
        return result.Value;
    }
}
