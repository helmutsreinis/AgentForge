using AgentForge.Abstractions.Models;
using Microsoft.Extensions.DependencyInjection;

namespace AgentForge.Models;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAgentForgeModels(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IModelContextPreparer, ModelContextPreparer>();
        services.AddSingleton<IModelProviderCatalog>(_ => ModelProviderCatalog.Create([]).Value);
        return services;
    }
}
