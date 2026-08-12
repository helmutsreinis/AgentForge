using AgentForge.Abstractions.Models;
using AgentForge.Abstractions.Providers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentForge.Models;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAgentForgeModels(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IModelContextPreparer, ModelContextPreparer>();
        services.AddSingleton<IModelCatalogDiscoveryService, OpenAiCompatibleModelDiscoveryService>();
        services.Replace(ServiceDescriptor.Scoped<IProviderProfileValidator, ModelProviderProfileValidator>());
        services.AddSingleton<IModelProviderCatalog>(_ => ModelProviderCatalog.Create([]).Value);
        services.TryAddSingleton<IModelProviderHealthSource>(_ => ModelProviderHealthCatalog.Create([]).Value);
        services.AddSingleton<IModelRouter, ModelRouter>();
        services.AddScoped<IModelRoutePlanner, ModelRoutePlanner>();
        services.AddScoped<IModelRunAdmissionService, ModelRunAdmissionService>();
        services.AddScoped<IModelRunExecutionService, ModelRunExecutionService>();
        services.AddScoped<IModelRunRecoveryService, ModelRunRecoveryService>();
        return services;
    }
}
