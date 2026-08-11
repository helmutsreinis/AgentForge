using AgentForge.Domain.Devices;

namespace AgentForge.Abstractions.Devices;

public sealed record PassiveSerialCandidate(
    string Endpoint,
    string Platform,
    string IdentityEvidence,
    string? VendorId,
    string? ProductId,
    string? SerialNumber,
    SerialDeviceReadiness Readiness,
    string? ReadinessReason);

public interface IPassiveSerialInventorySource
{
    ValueTask<IReadOnlyList<PassiveSerialCandidate>> InspectAsync(CancellationToken cancellationToken);
}

public interface ISerialDiscoveryService
{
    ValueTask<SerialInventorySnapshot> InspectAsync(CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<SerialInventoryChange>> InspectChangesAsync(CancellationToken cancellationToken);
}

public interface IDeviceCapabilityAuthorizer
{
    ValueTask<bool> IsAllowedAsync(
        DeviceCapabilityGrant grant,
        DeviceCapability capability,
        CancellationToken cancellationToken);
}
