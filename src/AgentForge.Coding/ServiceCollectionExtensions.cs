using AgentForge.Abstractions.Coding;
using Microsoft.Extensions.DependencyInjection;

namespace AgentForge.Coding;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAgentForgeCoding(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IRepositoryDiscovery, RepositoryDiscovery>();
        services.AddSingleton<RoslynSemanticNavigator>();
        services.AddSingleton<ISemanticNavigator>(provider => provider.GetRequiredService<RoslynSemanticNavigator>());
        services.AddSingleton<ILanguageServerAdapter>(provider => provider.GetRequiredService<RoslynSemanticNavigator>());
        services.AddSingleton<ICodingWorkspaceManager, GitCodingWorkspaceManager>();
        services.AddSingleton<ICodingPatchApplier, HashBoundPatchApplier>();
        services.AddScoped<ICodingVerifier, SandboxCodingVerifier>();
        services.AddScoped<ICodingBackendCatalog, CodingBackendCatalog>();
        services.AddSingleton<ICodingReviewer, GitCodingReviewer>();
        services.AddScoped<ICodingSessionService, CodingSessionService>();
        return services;
    }
}
