using AgentForge.Abstractions.Security;
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
            .ValidateOnStart();
        services.AddSingleton<ISensitiveDataRedactor, StructuredSensitiveDataRedactor>();
        return services;
    }
}
