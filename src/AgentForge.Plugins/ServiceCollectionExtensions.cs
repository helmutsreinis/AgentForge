using AgentForge.Abstractions.Plugins;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentForge.Plugins;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAgentForgePlugins(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddOptions<PluginOptions>()
            .Bind(configuration.GetSection(PluginOptions.SectionName))
            .Validate(options => options.MaximumPackages is >= 0 and <= 1024 &&
                options.MaximumManifestBytes is >= 1024 and <= 1_048_576 &&
                options.MaximumAssemblyBytes is >= 1024 and <= 536_870_912 &&
                ConfiguredPluginSignatureVerifier.ValidateKeys(options.TrustedPublicKeys),
                "Plugin catalog limits are outside safe bounds")
            .ValidateOnStart();
        services.TryAddSingleton<IPluginSignatureVerifier, ConfiguredPluginSignatureVerifier>();
        services.TryAddSingleton<IPluginWorkerLauncher, SandboxPluginWorkerLauncher>();
        services.AddSingleton<IPluginCatalog, FilePluginCatalog>();
        services.AddSingleton<IPluginLoader, PluginLoader>();
        return services;
    }
}
