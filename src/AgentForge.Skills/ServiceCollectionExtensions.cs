using AgentForge.Abstractions.Setup;
using AgentForge.Abstractions.Skills;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentForge.Skills;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAgentForgeSkills(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<ISkillSignatureVerifier, RejectingSkillSignatureVerifier>();
        services.AddSingleton<ISkillPackageLoader, PortableSkillPackageLoader>();
        services.AddScoped<ISkillRegistryService, SkillRegistryService>();
        services.AddScoped<ISkillGovernanceService, SkillGovernanceService>();
        services.AddScoped<ISkillSnapshotService, SkillSnapshotService>();
        services.AddScoped<IRecoveryConfigurationInspector, SkillRecoveryConfigurationInspector>();
        return services;
    }
}
