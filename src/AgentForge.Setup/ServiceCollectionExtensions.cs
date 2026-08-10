using AgentForge.Abstractions.Installations;
using AgentForge.Abstractions.Providers;
using AgentForge.Abstractions.Setup;
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
        services.AddSingleton<IDataDirectoryProvider, DefaultDataDirectoryProvider>();
        services.AddSingleton<IInstallationStateReader, FileInstallationStateReader>();
        services.AddScoped<ISetupApplicationService, SetupApplicationService>();
        services.AddScoped<IProviderProfileValidator, DeterministicProviderProfileValidator>();
        return services;
    }
}
