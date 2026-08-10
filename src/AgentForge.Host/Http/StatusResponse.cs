using AgentForge.Domain.Installations;

namespace AgentForge.Host.Http;

public sealed record StatusResponse(
    string Product,
    string InstallationState,
    bool Ready,
    long Version,
    string CorrelationId,
    string? RecoveryReason)
{
    public static StatusResponse From(InstallationSnapshot snapshot, string correlationId) => new(
        "AgentForge",
        snapshot.State.ToString(),
        snapshot.IsReady,
        snapshot.Version,
        correlationId,
        snapshot.RecoveryReason);
}
