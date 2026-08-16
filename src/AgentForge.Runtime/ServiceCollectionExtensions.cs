using AgentForge.Abstractions.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentForge.Runtime;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAgentForgeRuntime(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddScoped<IAgentLoopStepExecutor, UnavailableAgentLoopStepExecutor>();
        services.AddScoped<IAgentLoopService, AgentLoopService>();
        services.AddScoped<IRunConversationService, RunConversationService>();
        return services;
    }
}
