using AgentForge.Abstractions.Mcp;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace AgentForge.Mcp;

public static class ServiceCollectionExtensions
{
    public static IMcpServerBuilder AddAgentForgeMcp(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddOptions<McpExposureOptions>()
            .Bind(configuration.GetSection(McpExposureOptions.SectionName))
            .Validate(options => options.MaximumResultCharacters is >= 256 and <= 65_536,
                "MaximumResultCharacters is outside the safe bound")
            .Validate(options => IsList(options.AllowedTools) && IsList(options.AllowedResources),
                "MCP allowlists must be bounded and exact")
            .ValidateOnStart();
        services.AddHttpContextAccessor();
        services.AddScoped<IMcpCallerContext, McpCallerContext>();
        services.AddSingleton<IMcpExposurePolicy, ConfiguredMcpExposurePolicy>();
        return services.AddMcpServer()
            .WithTools<AgentForgeMcpTools>()
            .WithResources<AgentForgeMcpResources>();
    }

    private static bool IsList(string[] values) => values.Length <= 128 &&
        values.All(value => value.Length is > 0 and <= 256 && !value.Any(char.IsControl)) &&
        values.Distinct(StringComparer.Ordinal).Count() == values.Length;
}
