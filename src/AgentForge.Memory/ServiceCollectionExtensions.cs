using AgentForge.Abstractions.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace AgentForge.Memory;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAgentForgeMemory(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddScoped<IMemoryService, MemoryService>();
        return services;
    }
}
