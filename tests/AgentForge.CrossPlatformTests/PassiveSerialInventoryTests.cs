using AgentForge.Abstractions.Devices;
using AgentForge.Abstractions.Time;
using AgentForge.Devices;
using Microsoft.Extensions.DependencyInjection;

namespace AgentForge.CrossPlatformTests;

public sealed class PassiveSerialInventoryTests
{
    [Fact]
    public async Task Native_inventory_is_bounded_valid_and_passive_on_the_current_platform()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IClock, SystemTestClock>();
        services.AddAgentForgeDevices();
        await using var provider = services.BuildServiceProvider();

        var snapshot = await provider.GetRequiredService<ISerialDiscoveryService>()
            .InspectAsync(CancellationToken.None);

        Assert.True(snapshot.IsValid());
        Assert.True(snapshot.Devices.Length <= 4096);
        Assert.Equal(snapshot.Devices.Length, snapshot.Devices.Select(device => device.Endpoint).Distinct(StringComparer.Ordinal).Count());
        Assert.All(snapshot.Devices, device => Assert.True(
            device.Platform == "windows" || device.Platform == "linux"));
    }

    private sealed class SystemTestClock : IClock { public DateTimeOffset UtcNow => DateTimeOffset.UtcNow; }
}
