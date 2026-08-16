using AgentForge.Abstractions.Search;
using AgentForge.Abstractions.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentForge.Search;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAgentForgeSearch(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IResearchCache, InMemoryResearchCache>();
        services.AddScoped<IBraveSearchConnectivityProbe, BraveSearchConnectivityProbe>();
        services.AddScoped<IBraveSearchProviderConfigurationService, BraveSearchProviderConfigurationService>();
        services.AddScoped<ISearchProvider, ManagedBraveSearchProvider>();
        services.AddScoped<IResearchService, ResearchService>();
        services.AddScoped<IBuiltInToolHandler, BraveSearchBuiltInToolHandler>();
        return services;
    }
}
