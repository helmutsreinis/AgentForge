using AgentForge.Domain.Primitives;

namespace AgentForge.Domain.Installations;

public sealed record InstallationSnapshot(
    InstallationId Id,
    InstallationState State,
    long Version,
    DateTimeOffset UpdatedAt,
    ActorId ActorId,
    CorrelationId CorrelationId,
    string? RecoveryReason)
{
    public bool IsReady => State is InstallationState.Ready;

    public static InstallationSnapshot CreateUninitialized(
        InstallationId id,
        DateTimeOffset createdAt,
        ActorId actorId,
        CorrelationId correlationId) =>
        new(id, InstallationState.Uninitialized, 0, createdAt, actorId, correlationId, null);
}
