using AgentForge.Domain.Artifacts;
using AgentForge.Domain.Installations;
using AgentForge.Domain.Primitives;

namespace AgentForge.Domain.Setup;

public enum DoctorCheckStatus
{
    Pass,
    Warning,
    Fail,
}

public sealed record DoctorCheck(
    string CheckId,
    DoctorCheckStatus Status,
    string Summary);

public sealed record SetupDoctorReport(
    DateTimeOffset GeneratedAt,
    InstallationSnapshot Installation,
    IReadOnlyList<DoctorCheck> Checks)
{
    public bool IsHealthy => Checks.All(item => item.Status is not DoctorCheckStatus.Fail);
}

public sealed record DoctorRequest(
    ActorId ActorId,
    CorrelationId CorrelationId);

public enum SetupProfileSnapshotKind
{
    SetupReport,
    Rollback,
}

public readonly record struct SetupProfileSnapshotId(Guid Value)
{
    public override string ToString() => Value.ToString("D");
}

public sealed record SetupProfileSnapshot(
    SetupProfileSnapshotId Id,
    InstallationId InstallationId,
    long ProfileVersion,
    SetupProfileSnapshotKind Kind,
    ArtifactReference Artifact,
    DateTimeOffset CreatedAt,
    ActorId ActorId,
    CorrelationId CorrelationId);

public sealed record ExportSetupProfileRequest(
    long ExpectedInstallationVersion,
    ActorId ActorId,
    CorrelationId CorrelationId,
    ReadOnlyMemory<char> AdministratorCredential);

public sealed record ExportSetupProfileResult(
    SetupProfileSnapshot Report,
    SetupProfileSnapshot Rollback,
    int RedactionCount);

public sealed record EnterRecoveryRequest(
    long ExpectedInstallationVersion,
    string Reason,
    ActorId ActorId,
    CorrelationId CorrelationId,
    ReadOnlyMemory<char> AdministratorCredential);

public sealed record ResumeRecoveryRequest(
    long ExpectedInstallationVersion,
    ActorId ActorId,
    CorrelationId CorrelationId,
    ReadOnlyMemory<char> AdministratorCredential);

public sealed record RecoveryTransitionResult(
    InstallationSnapshot Installation,
    SetupProfileSnapshot? RollbackSnapshot = null);
