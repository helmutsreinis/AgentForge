using AgentForge.Domain.Installations;
using AgentForge.Domain.Primitives;

namespace AgentForge.Abstractions.Mcp;

public enum McpTransportKind
{
    Stdio,
    StreamableHttp,
}

public enum McpExposureKind
{
    Tool,
    Resource,
}

public sealed record McpExposureRequest(
    McpTransportKind Transport,
    McpExposureKind Kind,
    string Name,
    InstallationSnapshot Installation,
    ActorId ActorId);

public interface IMcpExposurePolicy
{
    DomainResult<bool> Evaluate(McpExposureRequest request);
}

public interface IMcpCallerContext
{
    McpTransportKind Transport { get; }

    ActorId ActorId { get; }
}

public static class McpCallerContextItems
{
    public const string Actor = "AgentForge.Mcp.Actor";
}
