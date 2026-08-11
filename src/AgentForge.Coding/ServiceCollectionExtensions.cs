using AgentForge.Abstractions.Coding;
using Microsoft.Extensions.DependencyInjection;

namespace AgentForge.Coding;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAgentForgeCoding(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IRepositoryDiscovery, RepositoryDiscovery>();
        services.AddSingleton<ISemanticNavigator, RoslynSemanticNavigator>();
        services.AddSingleton<ICodingWorkspaceManager, GitCodingWorkspaceManager>();
        return services;
    }
}
