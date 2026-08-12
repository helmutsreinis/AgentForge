using AgentForge.Abstractions.Devices;
using AgentForge.Abstractions.Time;
using AgentForge.Domain.Devices;

namespace AgentForge.Devices;

internal sealed class DeviceCapabilityAuthorizer(IClock clock) : IDeviceCapabilityAuthorizer
{
    public ValueTask<bool> IsAllowedAsync(
        DeviceCapabilityGrant grant,
        DeviceCapability capability,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(grant.Allows(capability, clock.UtcNow));
    }
}
