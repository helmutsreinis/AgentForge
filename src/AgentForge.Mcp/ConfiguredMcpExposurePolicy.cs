using AgentForge.Abstractions.Mcp;
using AgentForge.Domain.Installations;
using AgentForge.Domain.Primitives;
using Microsoft.Extensions.Options;

namespace AgentForge.Mcp;

internal sealed class ConfiguredMcpExposurePolicy(IOptions<McpExposureOptions> options) : IMcpExposurePolicy
{
    public DomainResult<bool> Evaluate(McpExposureRequest request)
    {
        if (!request.Installation.IsReady || request.ActorId.Value.Length is < 1 or > 256 ||
            request.Name.Length is < 1 or > 256 || request.Name.Any(char.IsControl))
            return Denied("MCP exposure requires Ready installation and bounded caller identity.");
        var allowed = request.Kind switch
        {
            McpExposureKind.Tool => options.Value.AllowedTools,
            McpExposureKind.Resource => options.Value.AllowedResources,
            _ => [],
        };
        return allowed.Contains(request.Name, StringComparer.Ordinal)
            ? DomainResult.Success(true)
            : Denied("MCP exposure is absent from the exact configured allowlist.");
    }

    private static DomainResult<bool> Denied(string message) => DomainResult.Fail<bool>(
        new DomainFailure(FailureCode.PolicyDenied, message));
}
