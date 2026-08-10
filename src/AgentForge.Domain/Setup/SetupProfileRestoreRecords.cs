using AgentForge.Domain.Installations;
using AgentForge.Domain.Primitives;

namespace AgentForge.Domain.Setup;

public sealed record PreviewSetupProfileRestoreRequest(
    SetupProfileSnapshotId SnapshotId,
    long ExpectedInstallationVersion,
    ActorId ActorId,
    CorrelationId CorrelationId,
    ReadOnlyMemory<char> AdministratorCredential);

public sealed record SetupProfileRestorePreview(
    SetupProfileSnapshot Snapshot,
    IReadOnlyList<SetupProfileChange> Changes,
    string RequestHash);

public sealed record ApplySetupProfileRestoreRequest(
    SetupProfileSnapshotId SnapshotId,
    long ExpectedInstallationVersion,
    string ExpectedRequestHash,
    ActorId ActorId,
    CorrelationId CorrelationId,
    ReadOnlyMemory<char> AdministratorCredential);

public sealed record SetupProfileRestoreResult(
    InstallationSnapshot Installation,
    SetupProfileSnapshot Snapshot,
    int RestoredProviderCount,
    int RestoredAgentCount,
    IReadOnlyList<SetupProfileChange> Changes,
    string RequestHash);
