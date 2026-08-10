using AgentForge.Abstractions.Environments;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AgentForge.Environment;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAgentForgeEnvironment(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<EnvironmentInventoryOptions>()
            .Bind(configuration.GetSection(EnvironmentInventoryOptions.SectionName))
            .Validate(options => options.MaximumPathDirectories is >= 1 and <= 256)
            .Validate(options => options.MaximumFilesPerDirectory is >= 1 and <= 16_384)
            .Validate(options => options.MaximumExecutables is >= 1 and <= 20_000)
            .ValidateOnStart();
        services.AddScoped<IEnvironmentProfiler, SystemEnvironmentProfiler>();
        services.AddScoped<IEnvironmentInventoryService, EnvironmentInventoryService>();
        return services;
    }
}
