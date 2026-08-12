using System.Collections.Immutable;
using AgentForge.Abstractions.Devices;
using AgentForge.Abstractions.Time;
using AgentForge.Devices;
using AgentForge.Domain.Devices;
using Microsoft.Extensions.DependencyInjection;

namespace AgentForge.UnitTests;

public sealed class SerialDiscoveryTests
{
    [Fact]
    public void Conservative_profile_never_asserts_control_lines()
    {
        var profile = SerialProfile.ConservativeDefault;

        Assert.True(profile.IsValid());
        Assert.False(profile.DtrEnable);
        Assert.False(profile.RtsEnable);
        Assert.Equal(SerialFlowControl.None, profile.FlowControl);
        Assert.False((profile with { BaudRate = 0 }).IsValid());
    }

    [Fact]
    public async Task Passive_inventory_keeps_physical_identity_across_reenumeration()
    {
        var source = new MutableInventorySource([
            Candidate("COM7", SerialDeviceReadiness.Ready),
        ]);
        await using var provider = Services(source).BuildServiceProvider();
        var discovery = provider.GetRequiredService<ISerialDiscoveryService>();

        var first = Assert.Single(await discovery.InspectChangesAsync(CancellationToken.None));
        source.Devices = [Candidate("COM11", SerialDeviceReadiness.PermissionRequired)];
        var second = Assert.Single(await discovery.InspectChangesAsync(CancellationToken.None));
        source.Devices = [];
        var third = Assert.Single(await discovery.InspectChangesAsync(CancellationToken.None));

        Assert.Equal(SerialInventoryChangeKind.Attached, first.Kind);
        Assert.Equal(SerialInventoryChangeKind.Reenumerated, second.Kind);
        Assert.Equal(first.PhysicalId, second.PhysicalId);
        Assert.Equal("COM7", second.PreviousEndpoint);
        Assert.Equal("COM11", second.CurrentEndpoint);
        Assert.Equal(SerialInventoryChangeKind.Detached, third.Kind);
        Assert.Equal(3, source.InspectionCount);
        Assert.Equal(0, source.OpenCount);
        Assert.Equal(0, source.BytesWritten);
    }

    [Fact]
    public async Task Capability_grants_are_exact_and_expiring()
    {
        var now = new DateTimeOffset(2026, 8, 12, 12, 0, 0, TimeSpan.Zero);
        var source = new MutableInventorySource([]);
        await using var provider = Services(source, now).BuildServiceProvider();
        var authorizer = provider.GetRequiredService<IDeviceCapabilityAuthorizer>();
        var id = SerialDeviceRecordValidator.PhysicalIdFromEvidence("windows", "usb:1234:5678:serial-1");
        var grant = new DeviceCapabilityGrant(id, new[] { DeviceCapability.Capture }.ToImmutableSortedSet(),
            now.AddMinutes(1), Hash('a'));

        Assert.True(await authorizer.IsAllowedAsync(grant, DeviceCapability.Capture, CancellationToken.None));
        Assert.False(await authorizer.IsAllowedAsync(grant, DeviceCapability.Read, CancellationToken.None));
        Assert.False(await authorizer.IsAllowedAsync(grant, DeviceCapability.Write, CancellationToken.None));
        Assert.False(await authorizer.IsAllowedAsync(grant with { ExpiresAtUtc = now }, DeviceCapability.Capture, CancellationToken.None));
    }

    private static ServiceCollection Services(MutableInventorySource source, DateTimeOffset? now = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IClock>(new FixedClock(now ?? new DateTimeOffset(2026, 8, 12, 12, 0, 0, TimeSpan.Zero)));
        services.AddSingleton<IPassiveSerialInventorySource>(source);
        services.AddAgentForgeDevices();
        return services;
    }

    private static PassiveSerialCandidate Candidate(string endpoint, SerialDeviceReadiness readiness) =>
        new(endpoint, "windows", "usb:1234:5678:stable-serial", "1234", "5678", "stable-serial", readiness, null);

    private static string Hash(char value) => $"sha256:{new string(value, 64)}";

    private sealed class FixedClock(DateTimeOffset now) : IClock { public DateTimeOffset UtcNow => now; }

    private sealed class MutableInventorySource(IReadOnlyList<PassiveSerialCandidate> devices) : IPassiveSerialInventorySource
    {
        public IReadOnlyList<PassiveSerialCandidate> Devices { get; set; } = devices;
        public int InspectionCount { get; private set; }
        public int OpenCount { get; private set; }
        public int BytesWritten { get; private set; }

        public ValueTask<IReadOnlyList<PassiveSerialCandidate>> InspectAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            InspectionCount++;
            return ValueTask.FromResult(Devices);
        }
    }
}
