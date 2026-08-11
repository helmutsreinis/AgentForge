using System.Collections.Immutable;
using System.Security.Cryptography;
using AgentForge.Abstractions.Devices;
using AgentForge.Abstractions.Time;
using AgentForge.Domain.Devices;
using AgentForge.Domain.Primitives;

namespace AgentForge.Devices;

internal sealed class SerialSessionService(
    ISerialTransportCatalog transports,
    IDeviceCapabilityAuthorizer authorizer,
    IClock clock) : ISerialSessionService
{
    public async Task<DomainResult<SerialReadResult>> ReadAsync(SerialReadRequest request, CancellationToken cancellationToken)
    {
        if (!Valid(request.Device, request.Profile, request.Grant) || request.MaximumBytes is < 1 or > 1_048_576)
            return Invalid<SerialReadResult>();
        if (!await Allowed(request.Device, request.Grant, DeviceCapability.Read, cancellationToken))
            return Denied<SerialReadResult>("An exact read grant is required.");
        var adapter = transports.Resolve(request.Device.Platform);
        if (adapter is null) return Unsupported<SerialReadResult>();
        SerialTransportChunk chunk;
        try { chunk = await adapter.ReadAsync(new(request.Device, request.Profile), request.MaximumBytes, cancellationToken); }
        catch (IOException)
        {
            return DomainResult.Fail<SerialReadResult>(new(
                FailureCode.RecoverableExternalFailure, "Serial read failed before a bounded result was confirmed."));
        }
        if (chunk.Bytes.Length > request.MaximumBytes || chunk.DroppedBefore < 0)
            return Invalid<SerialReadResult>();
        var bytes = chunk.Bytes.ToArray().ToImmutableArray();
        return DomainResult.Success(new SerialReadResult(request.Device.PhysicalId, bytes, chunk.DroppedBefore,
            chunk.DisconnectedAfter, Hash(bytes.AsSpan())));
    }

    public async Task<DomainResult<SerialWriteReceipt>> WriteAsync(SerialWriteRequest request, CancellationToken cancellationToken)
    {
        if (!Valid(request.Device, request.Profile, request.Grant) || request.Bytes.Length is < 1 or > 1_048_576)
            return Invalid<SerialWriteReceipt>();
        if (!await Allowed(request.Device, request.Grant, DeviceCapability.Write, cancellationToken))
            return Denied<SerialWriteReceipt>("An exact write grant is required.");
        var adapter = transports.Resolve(request.Device.Platform);
        if (adapter is null) return Unsupported<SerialWriteReceipt>();
        int count;
        try { count = await adapter.WriteAsync(new(request.Device, request.Profile), request.Bytes.AsMemory(), cancellationToken); }
        catch (IOException)
        {
            return DomainResult.Fail<SerialWriteReceipt>(new(
                FailureCode.RecoverableExternalFailure, "Serial write outcome is unavailable and must not be retried automatically."));
        }
        if (count != request.Bytes.Length) return DomainResult.Fail<SerialWriteReceipt>(new(
            FailureCode.RecoverableExternalFailure, "Serial transport did not confirm the exact write length."));
        return DomainResult.Success(new SerialWriteReceipt(request.Device.PhysicalId, count,
            Hash(request.Bytes.AsSpan()), clock.UtcNow));
    }

    private async ValueTask<bool> Allowed(
        SerialDeviceDescriptor device, DeviceCapabilityGrant grant, DeviceCapability capability, CancellationToken token) =>
        grant.PhysicalId == device.PhysicalId && await authorizer.IsAllowedAsync(grant, capability, token);

    private static bool Valid(SerialDeviceDescriptor device, SerialProfile profile, DeviceCapabilityGrant grant) =>
        device is not null && device.IsValid() && profile is not null && profile.IsValid() && grant is not null;

    private static string Hash(ReadOnlySpan<byte> value) =>
        $"sha256:{Convert.ToHexStringLower(SHA256.HashData(value))}";
    private static DomainResult<T> Invalid<T>() => DomainResult.Fail<T>(new(FailureCode.ValidationFailure, "Serial session request is invalid."));
    private static DomainResult<T> Denied<T>(string message) => DomainResult.Fail<T>(new(FailureCode.PolicyDenied, message));
    private static DomainResult<T> Unsupported<T>() => DomainResult.Fail<T>(new(FailureCode.UnsupportedCapability, "No approved serial transport is installed for this platform."));
}
