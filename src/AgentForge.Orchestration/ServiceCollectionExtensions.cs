using AgentForge.Abstractions.Orchestration;
using AgentForge.Abstractions.Scheduling;
using Microsoft.Extensions.DependencyInjection;

namespace AgentForge.Orchestration;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAgentForgeOrchestration(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddScoped<ITaskOrchestrator, TaskOrchestrator>();
        services.AddScoped<IDelegationPlanner, DelegationPlanner>();
        services.AddScoped<IDelegationService, DelegationService>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ITimeZoneResolver, SystemTimeZoneResolver>();
        services.AddScoped<IScheduleService, ScheduleService>();
        services.AddHostedService<ScheduleDispatcherWorker>();
        return services;
    }
}
