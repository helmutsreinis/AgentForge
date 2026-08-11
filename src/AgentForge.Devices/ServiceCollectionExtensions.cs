using AgentForge.Abstractions.Devices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentForge.Devices;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAgentForgeDevices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IPassiveSerialInventorySource, SystemPassiveSerialInventorySource>();
        services.TryAddSingleton<ISerialTransportCatalog>(_ => new SerialTransportCatalog([]));
        services.AddSingleton<ISerialDiscoveryService, PassiveSerialDiscoveryService>();
        services.AddSingleton<IDeviceCapabilityAuthorizer, DeviceCapabilityAuthorizer>();
        services.AddScoped<ISerialCaptureService, SerialCaptureService>();
        services.AddScoped<ISerialSessionService, SerialSessionService>();
        return services;
    }
}
