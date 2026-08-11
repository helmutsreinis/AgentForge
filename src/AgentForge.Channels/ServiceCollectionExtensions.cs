using AgentForge.Abstractions.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentForge.Channels;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAgentForgeChannels(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IChannelAdapterCatalog>(_ => new ChannelAdapterCatalog([]));
        services.TryAddSingleton<IChannelAttachmentScanner, RejectingAttachmentScanner>();
        services.AddScoped<IChannelService, ChannelService>();
        return services;
    }
}
