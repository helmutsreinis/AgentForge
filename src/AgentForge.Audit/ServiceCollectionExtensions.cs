using AgentForge.Abstractions.Auditing;
using Microsoft.Extensions.DependencyInjection;

namespace AgentForge.Audit;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAgentForgeAudit(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddScoped<IAuditRecorder, AuditRecorder>();
        services.AddScoped<IAuditIntegrityVerifier, AuditIntegrityVerifier>();
        return services;
    }
}
