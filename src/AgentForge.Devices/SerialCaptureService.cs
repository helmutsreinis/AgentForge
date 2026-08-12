using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using AgentForge.Abstractions.Artifacts;
using AgentForge.Abstractions.Auditing;
using AgentForge.Abstractions.Devices;
using AgentForge.Abstractions.Persistence;
using AgentForge.Abstractions.Time;
using AgentForge.Domain.Auditing;
using AgentForge.Domain.Devices;
using AgentForge.Domain.Primitives;

namespace AgentForge.Devices;

internal sealed class SerialCaptureService(
    ISerialCaptureRepository repository,
    ISerialTransportCatalog transports,
    IDeviceCapabilityAuthorizer authorizer,
    IArtifactStore artifacts,
    IAuditRecorder audit,
    IUnitOfWork unitOfWork,
    IClock clock) : ISerialCaptureService
{
    public async Task<DomainResult<SerialCaptureRecord>> CaptureAsync(
        CreateSerialCaptureRequest request,
        CancellationToken cancellationToken)
    {
        if (!Valid(request)) return Invalid<SerialCaptureRecord>("Serial capture request or bounds are invalid.");
        var requestHash = RequestHash(request);
        var existing = await repository.FindByIdempotencyKeyAsync(request.InstallationId, request.IdempotencyKey, cancellationToken);
        if (existing is not null)
            return existing.Id == request.Id && existing.RequestHash == requestHash
                ? DomainResult.Success(existing)
                : Conflict<SerialCaptureRecord>("The serial capture idempotency key is bound to different input.");
        if (request.Grant.PhysicalId != request.Device.PhysicalId ||
            !await authorizer.IsAllowedAsync(request.Grant, DeviceCapability.Capture, cancellationToken))
            return Denied<SerialCaptureRecord>("An exact unexpired capture grant is required for this physical device.");
        var adapter = transports.Resolve(request.Device.Platform);
        if (adapter is null) return Unsupported<SerialCaptureRecord>("No approved serial transport is installed for this platform.");

        var started = clock.UtcNow;
        var frames = new List<SerialCaptureFrame>();
        long captured = 0;
        long dropped = 0;
        long previousTicks = -1;
        var observedChunks = 0;
        var truncated = false;
        var disconnected = false;
        try
        {
            await foreach (var chunk in adapter.CaptureAsync(new(request.Device, request.Profile), cancellationToken)
                .WithCancellation(cancellationToken))
            {
                if (++observedChunks > 65_536)
                {
                    truncated = true;
                    break;
                }
                if (chunk.Offset < TimeSpan.Zero || chunk.Offset.Ticks < previousTicks ||
                    chunk.DroppedBefore < 0 || chunk.Bytes.Length > 1_048_576)
                    return Invalid<SerialCaptureRecord>("Serial transport returned invalid timing, drop, or frame evidence.");
                if (chunk.Offset > request.MaximumDuration)
                {
                    truncated = true;
                    break;
                }
                var remaining = request.MaximumBytes - captured;
                var take = (int)Math.Min(remaining, chunk.Bytes.Length);
                dropped = checked(dropped + chunk.DroppedBefore);
                if (take > 0 || chunk.DroppedBefore > 0 || chunk.DisconnectedAfter)
                {
                    var bytes = chunk.Bytes[..take].ToArray().ToImmutableArray();
                    frames.Add(new(chunk.Offset.Ticks, bytes, chunk.DroppedBefore, chunk.DisconnectedAfter));
                    captured += take;
                    previousTicks = chunk.Offset.Ticks;
                    disconnected |= chunk.DisconnectedAfter;
                }
                if (take != chunk.Bytes.Length || captured == request.MaximumBytes)
                {
                    truncated = true;
                    break;
                }
                if (chunk.DisconnectedAfter) break;
            }
        }
        catch (IOException)
        {
            return Recoverable<SerialCaptureRecord>("The serial capture transport failed before durable completion.");
        }

        var streamHash = SerialCaptureCodec.HashFrames(frames);
        await using var encoded = SerialCaptureCodec.Encode(request.Device.PhysicalId, frames);
        AgentForge.Domain.Artifacts.ArtifactReference artifact;
        try
        {
            artifact = await artifacts.PutAsync(encoded, "application/vnd.agentforge.serial-capture", cancellationToken);
        }
        catch (IOException)
        {
            return Recoverable<SerialCaptureRecord>("The serial capture artifact could not be stored durably.");
        }
        var completed = clock.UtcNow;
        var record = new SerialCaptureRecord(
            request.Id, request.InstallationId, request.AgentId, request.Device.PhysicalId,
            request.Device.EvidenceHash, request.Profile with { }, started, completed, captured, dropped,
            frames.Count, truncated, disconnected, artifact, streamHash, requestHash, request.IdempotencyKey.Trim(),
            request.ActorId, request.CorrelationId, request.CausationId, 0);
        if (!record.IsValid()) return Invalid<SerialCaptureRecord>("Serial capture result failed integrity validation.");
        await repository.AddAsync(record, cancellationToken);
        await audit.RecordAsync(new AuditRecordRequest(
            request.InstallationId, request.ActorId, request.CorrelationId, request.CausationId,
            "serial.capture.completed", AuditOutcome.Succeeded,
            new { CaptureId = request.Id.ToString(), DeviceId = request.Device.PhysicalId.Value, request.MaximumBytes },
            new { record.CapturedBytes, record.DroppedBytes, record.Truncated, record.Disconnected, record.StreamHash, ArtifactHash = artifact.ContentHash },
            null), cancellationToken);
        var commit = await unitOfWork.CommitAsync(cancellationToken);
        return commit.Succeeded ? DomainResult.Success(record) : DomainResult.Fail<SerialCaptureRecord>(commit.Failure!);
    }

    public async IAsyncEnumerable<SerialCaptureFrame> ReplayAsync(
        SerialCaptureRecord capture,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (capture is null || !capture.IsValid()) throw new InvalidDataException("Serial capture metadata is invalid.");
        await using var stream = await artifacts.OpenReadAsync(capture.Artifact, cancellationToken);
        var frames = await SerialCaptureCodec.DecodeAsync(stream, capture.PhysicalDeviceId, capture.Artifact.Length, cancellationToken);
        if (frames.Count != capture.FrameCount || SerialCaptureCodec.HashFrames(frames) != capture.StreamHash ||
            frames.Sum(frame => (long)frame.Bytes.Length) != capture.CapturedBytes ||
            frames.Sum(frame => frame.DroppedBefore) != capture.DroppedBytes)
            throw new InvalidDataException("Serial capture replay failed integrity validation.");
        foreach (var frame in frames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return frame;
        }
    }

    private bool Valid(CreateSerialCaptureRequest request) => request is not null &&
        request.Id.Value != Guid.Empty && request.InstallationId.Value != Guid.Empty && request.AgentId.Value != Guid.Empty &&
        request.Device is not null && request.Device.IsValid() && request.Profile is not null && request.Profile.IsValid() &&
        request.Grant is not null && request.MaximumBytes is >= 1 and <= 16_777_216 &&
        request.MaximumDuration >= TimeSpan.FromMilliseconds(1) && request.MaximumDuration <= TimeSpan.FromHours(1) &&
        SerialDeviceRecordValidator.Text(request.IdempotencyKey, 128) &&
        SerialDeviceRecordValidator.Text(request.ActorId.Value, 256) &&
        SerialDeviceRecordValidator.Text(request.CorrelationId.Value, 128) && request.Grant.ExpiresAtUtc > clock.UtcNow;

    private static string RequestHash(CreateSerialCaptureRequest request)
    {
        var profile = request.Profile;
        var value = string.Join('\n', request.Id.Value, request.InstallationId.Value, request.AgentId.Value,
            request.Device.PhysicalId.Value, request.Device.Endpoint, request.Device.EvidenceHash,
            profile.BaudRate, profile.DataBits, profile.Parity, profile.StopBits, profile.FlowControl,
            profile.DtrEnable, profile.RtsEnable, profile.ReadTimeoutMilliseconds, profile.WriteTimeoutMilliseconds,
            request.Grant.EvidenceHash, request.MaximumBytes, request.MaximumDuration.Ticks);
        return $"sha256:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))}";
    }

    private static DomainResult<T> Invalid<T>(string message) => DomainResult.Fail<T>(new(FailureCode.ValidationFailure, message));
    private static DomainResult<T> Conflict<T>(string message) => DomainResult.Fail<T>(new(FailureCode.ConcurrencyConflict, message));
    private static DomainResult<T> Denied<T>(string message) => DomainResult.Fail<T>(new(FailureCode.PolicyDenied, message));
    private static DomainResult<T> Unsupported<T>(string message) => DomainResult.Fail<T>(new(FailureCode.UnsupportedCapability, message));
    private static DomainResult<T> Recoverable<T>(string message) => DomainResult.Fail<T>(new(FailureCode.RecoverableExternalFailure, message));
}
