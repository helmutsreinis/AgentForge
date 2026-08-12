using System.ComponentModel;
using AgentForge.Abstractions.Installations;
using AgentForge.Abstractions.Mcp;
using ModelContextProtocol.Server;

namespace AgentForge.Mcp;

[McpServerToolType]
internal sealed class AgentForgeMcpTools(
    IInstallationStateReader installations,
    IMcpExposurePolicy policy,
    IMcpCallerContext caller)
{
    [McpServerTool(Name = "agentforge_status")]
    [Description("Returns bounded AgentForge installation readiness metadata without configuration or secrets.")]
    public async Task<object> StatusAsync(CancellationToken cancellationToken)
    {
        var installation = await installations.ReadAsync(cancellationToken);
        var decision = policy.Evaluate(new McpExposureRequest(
            caller.Transport, McpExposureKind.Tool, "agentforge_status", installation, caller.ActorId));
        if (!decision.IsSuccess) throw new UnauthorizedAccessException(decision.Failure!.Message);
        return new
        {
            installationId = installation.Id.Value,
            state = installation.State.ToString(),
            ready = installation.IsReady,
            version = installation.Version,
            updatedAt = installation.UpdatedAt,
        };
    }
}
