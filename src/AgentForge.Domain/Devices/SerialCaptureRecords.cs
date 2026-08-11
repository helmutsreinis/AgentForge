using System.Collections.Immutable;
using AgentForge.Domain.Agents;
using AgentForge.Domain.Artifacts;
using AgentForge.Domain.Primitives;

namespace AgentForge.Domain.Devices;

public readonly record struct SerialCaptureId(Guid Value)
{
    public override string ToString() => Value.ToString("D");
}

public sealed record SerialCaptureFrame(
    long OffsetTicks,
    ImmutableArray<byte> Bytes,
    long DroppedBefore,
    bool DisconnectedAfter)
{
    public bool IsValid() => OffsetTicks >= 0 && Bytes.Length <= 1_048_576 && DroppedBefore >= 0;
}

public sealed record SerialCaptureRecord(
    SerialCaptureId Id,
    InstallationId InstallationId,
    AgentIdentityId AgentId,
    PhysicalDeviceId PhysicalDeviceId,
    string EndpointEvidenceHash,
    SerialProfile Profile,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    long CapturedBytes,
    long DroppedBytes,
    int FrameCount,
    bool Truncated,
    bool Disconnected,
    ArtifactReference Artifact,
    string StreamHash,
    string RequestHash,
    string IdempotencyKey,
    ActorId ActorId,
    CorrelationId CorrelationId,
    CorrelationId? CausationId,
    long Version)
{
    public bool IsValid() => Id.Value != Guid.Empty && InstallationId.Value != Guid.Empty &&
        AgentId.Value != Guid.Empty && SerialDeviceRecordValidator.IsPhysicalId(PhysicalDeviceId) &&
        SerialDeviceRecordValidator.IsSha256(EndpointEvidenceHash) && Profile.IsValid() &&
        StartedAtUtc.Offset == TimeSpan.Zero && CompletedAtUtc.Offset == TimeSpan.Zero &&
        CompletedAtUtc >= StartedAtUtc && CapturedBytes >= 0 && DroppedBytes >= 0 &&
        FrameCount >= 0 && Artifact.Length >= 0 &&
        string.Equals(Artifact.MediaType, "application/vnd.agentforge.serial-capture", StringComparison.Ordinal) &&
        SerialDeviceRecordValidator.IsSha256(Artifact.ContentHash) &&
        SerialDeviceRecordValidator.IsSha256(StreamHash) && SerialDeviceRecordValidator.IsSha256(RequestHash) &&
        SerialDeviceRecordValidator.Text(IdempotencyKey, 128) && SerialDeviceRecordValidator.Text(ActorId.Value, 256) &&
        SerialDeviceRecordValidator.Text(CorrelationId.Value, 128) && Version >= 0;
}

public sealed record SerialReadResult(
    PhysicalDeviceId PhysicalDeviceId,
    ImmutableArray<byte> Bytes,
    long DroppedBytes,
    bool Disconnected,
    string ContentHash);

public sealed record SerialWriteReceipt(
    PhysicalDeviceId PhysicalDeviceId,
    int ByteCount,
    string ContentHash,
    DateTimeOffset CompletedAtUtc);
