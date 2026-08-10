using AgentForge.Abstractions.Installations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AgentForge.Setup;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAgentForgeSetup(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<InstallationOptions>()
            .Bind(configuration.GetSection(InstallationOptions.SectionName))
            .Validate(options => !string.IsNullOrWhiteSpace(options.StateFileName), "StateFileName is required")
            .ValidateOnStart();
        services.AddSingleton<IInstallationStateReader, FileInstallationStateReader>();
        return services;
    }
}
