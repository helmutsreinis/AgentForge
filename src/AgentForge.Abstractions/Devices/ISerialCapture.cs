using System.Collections.Immutable;
using AgentForge.Domain.Agents;
using AgentForge.Domain.Devices;
using AgentForge.Domain.Primitives;

namespace AgentForge.Abstractions.Devices;

public sealed record SerialTransportChunk(
    TimeSpan Offset,
    ReadOnlyMemory<byte> Bytes,
    long DroppedBefore,
    bool DisconnectedAfter);

public sealed record SerialTransportRequest(
    SerialDeviceDescriptor Device,
    SerialProfile Profile);

public interface ISerialTransportAdapter
{
    string AdapterId { get; }
    bool Supports(string platform);

    IAsyncEnumerable<SerialTransportChunk> CaptureAsync(
        SerialTransportRequest request,
        CancellationToken cancellationToken);

    ValueTask<SerialTransportChunk> ReadAsync(
        SerialTransportRequest request,
        int maximumBytes,
        CancellationToken cancellationToken);

    ValueTask<int> WriteAsync(
        SerialTransportRequest request,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken);
}

public interface ISerialTransportCatalog
{
    ISerialTransportAdapter? Resolve(string platform);
}

public sealed record CreateSerialCaptureRequest(
    SerialCaptureId Id,
    InstallationId InstallationId,
    AgentIdentityId AgentId,
    SerialDeviceDescriptor Device,
    SerialProfile Profile,
    DeviceCapabilityGrant Grant,
    int MaximumBytes,
    TimeSpan MaximumDuration,
    string IdempotencyKey,
    ActorId ActorId,
    CorrelationId CorrelationId,
    CorrelationId? CausationId);

public sealed record SerialReadRequest(
    SerialDeviceDescriptor Device,
    SerialProfile Profile,
    DeviceCapabilityGrant Grant,
    int MaximumBytes);

public sealed record SerialWriteRequest(
    SerialDeviceDescriptor Device,
    SerialProfile Profile,
    DeviceCapabilityGrant Grant,
    ImmutableArray<byte> Bytes);

public interface ISerialCaptureRepository
{
    ValueTask<SerialCaptureRecord?> FindByIdAsync(SerialCaptureId id, CancellationToken cancellationToken);
    ValueTask<SerialCaptureRecord?> FindByIdempotencyKeyAsync(
        InstallationId installationId, string idempotencyKey, CancellationToken cancellationToken);
    ValueTask AddAsync(SerialCaptureRecord capture, CancellationToken cancellationToken);
}

public interface ISerialCaptureService
{
    Task<DomainResult<SerialCaptureRecord>> CaptureAsync(
        CreateSerialCaptureRequest request,
        CancellationToken cancellationToken);

    IAsyncEnumerable<SerialCaptureFrame> ReplayAsync(
        SerialCaptureRecord capture,
        CancellationToken cancellationToken);
}

public interface ISerialSessionService
{
    Task<DomainResult<SerialReadResult>> ReadAsync(SerialReadRequest request, CancellationToken cancellationToken);
    Task<DomainResult<SerialWriteReceipt>> WriteAsync(SerialWriteRequest request, CancellationToken cancellationToken);
}
