using System.ComponentModel;
using System.Text.Json;
using AgentForge.Abstractions.Installations;
using AgentForge.Abstractions.Mcp;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace AgentForge.Mcp;

[McpServerResourceType]
internal sealed class AgentForgeMcpResources(
    IInstallationStateReader installations,
    IMcpExposurePolicy policy,
    IMcpCallerContext caller,
    IOptions<McpExposureOptions> options)
{
    [McpServerResource(UriTemplate = "agentforge://status", Name = "agentforge_status")]
    [Description("Bounded AgentForge installation readiness resource.")]
    public async Task<string> StatusAsync(CancellationToken cancellationToken)
    {
        var installation = await installations.ReadAsync(cancellationToken);
        var decision = policy.Evaluate(new McpExposureRequest(
            caller.Transport, McpExposureKind.Resource, "agentforge://status", installation, caller.ActorId));
        if (!decision.IsSuccess) throw new UnauthorizedAccessException(decision.Failure!.Message);
        var json = JsonSerializer.Serialize(new
        {
            installationId = installation.Id.Value,
            state = installation.State.ToString(),
            ready = installation.IsReady,
            version = installation.Version,
        });
        return json.Length <= options.Value.MaximumResultCharacters
            ? json
            : throw new InvalidDataException("MCP resource result exceeded its configured bound.");
    }
}
