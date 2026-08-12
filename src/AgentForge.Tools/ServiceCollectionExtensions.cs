using AgentForge.Abstractions.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AgentForge.Tools;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAgentForgeTools(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<RestrictedProcessOptions>()
            .Bind(configuration.GetSection(RestrictedProcessOptions.SectionName))
            .Validate(options => options.MaximumArguments is >= 1 and <= 4096)
            .Validate(options => options.MaximumArgumentCharacters is >= 1 and <= 1_048_576)
            .Validate(options => options.MaximumEnvironmentVariables is >= 0 and <= 1024)
            .Validate(options => options.MaximumEnvironmentValueCharacters is >= 1 and <= 1_048_576)
            .Validate(options => options.MaximumTimeoutSeconds is >= 1 and <= 3600)
            .Validate(options => options.MaximumOutputBytes is >= 1 and <= 16_777_216)
            .Validate(options => options.TerminationWaitSeconds is >= 1 and <= 60)
            .Validate(options => AreValidNames(options.AllowedInheritedEnvironmentVariables))
            .Validate(options => AreValidNames(options.AllowedInvocationEnvironmentVariables))
            .ValidateOnStart();
        services.AddOptions<DockerSandboxOptions>()
            .Bind(configuration.GetSection(DockerSandboxOptions.SectionName))
            .Validate(options =>
                (string.IsNullOrWhiteSpace(options.RuntimeExecutable) && string.IsNullOrWhiteSpace(options.ImageReference)) ||
                DockerContainerSandbox.IsConfigured(options))
            .ValidateOnStart();
        services.AddSingleton<RestrictedHostSandbox>();
        services.AddSingleton<IContainerRuntimeInvoker, RestrictedHostContainerRuntimeInvoker>();
        services.AddSingleton<DockerContainerSandbox>();
        services.AddSingleton<ISandbox, SelectingSandbox>();
        services.AddSingleton<IToolCatalog>(_ => ToolCatalog.Create([]).Value);
        services.AddScoped<IToolInvocationService, ToolInvocationService>();
        services.AddScoped<IToolAvailabilityProbeService, ToolAvailabilityProbeService>();
        return services;
    }

    private static bool AreValidNames(string[]? names)
    {
        return names is not null && names.Length <= 256 && names.All(IsValidName) &&
            names.Distinct(StringComparer.OrdinalIgnoreCase).Count() == names.Length;
    }

    private static bool IsValidName(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 128 &&
        (char.IsAsciiLetter(value[0]) || value[0] == '_') &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character == '_');
}
