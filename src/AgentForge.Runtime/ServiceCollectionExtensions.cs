using AgentForge.Abstractions.Runtime;
using AgentForge.Abstractions.Scheduling;
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
        services.AddScoped<IScheduledAgentRunService, ScheduledAgentRunService>();
        services.AddHostedService<ScheduledAgentRunWorker>();
        return services;
    }
}
