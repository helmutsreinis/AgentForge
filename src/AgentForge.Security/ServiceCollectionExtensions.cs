using AgentForge.Abstractions.Installations;
using AgentForge.Abstractions.Security;
using AgentForge.Abstractions.Time;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AgentForge.Security;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAgentForgeSecurity(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<SecurityOptions>()
            .Bind(configuration.GetSection(SecurityOptions.SectionName))
            .Validate(options => options.MaximumRedactionPayloadBytes is >= 1024 and <= 16_777_216,
                "MaximumRedactionPayloadBytes must be between 1 KiB and 16 MiB")
            .Validate(options => options.MaximumRedactionDepth is >= 4 and <= 64,
                "MaximumRedactionDepth must be between 4 and 64")
            .Validate(options => options.MaximumSecretCharacters is >= 16 and <= 1_048_576,
                "MaximumSecretCharacters must be between 16 and 1 MiB")
            .Validate(options => IsDirectoryName(options.SecretDirectoryName),
                "SecretDirectoryName must be a relative directory name")
            .ValidateOnStart();
        services.AddSingleton<ISensitiveDataRedactor, StructuredSensitiveDataRedactor>();
        services.AddSingleton<ISecretStore>(provider =>
        {
            if (OperatingSystem.IsWindows())
            {
                return new WindowsDpapiSecretStore(
                    provider.GetRequiredService<IDataDirectoryProvider>(),
                    provider.GetRequiredService<IIdentifierGenerator>(),
                    provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<SecurityOptions>>());
            }

            if (OperatingSystem.IsLinux())
            {
                return new LinuxSecretServiceStore(
                    provider.GetRequiredService<IIdentifierGenerator>(),
                    provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<SecurityOptions>>());
            }

            return new UnavailableSecretStore();
        });
        return services;
    }

    private static bool IsDirectoryName(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        !Path.IsPathRooted(value) &&
        !value.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Contains("..", StringComparer.Ordinal);
}
