using System.Text.Json;
using AgentForge.Domain.Installations;
using AgentForge.Domain.Mcp;
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

public interface IAgentForgeMcpRemoteClient : IAsyncDisposable
{
    Task<DomainResult<IReadOnlyList<McpRemoteTool>>> ListToolsAsync(CancellationToken cancellationToken);

    Task<DomainResult<IReadOnlyList<McpRemoteResource>>> ListResourcesAsync(CancellationToken cancellationToken);

    Task<DomainResult<McpRemoteResult>> CallToolAsync(
        string name,
        IReadOnlyDictionary<string, JsonElement> arguments,
        CancellationToken cancellationToken);

    Task<DomainResult<McpRemoteResult>> ReadResourceAsync(
        string uri,
        CancellationToken cancellationToken);
}

public interface IAgentForgeMcpRemoteClientFactory
{
    Task<DomainResult<IAgentForgeMcpRemoteClient>> ConnectAsync(
        McpRemoteServerProfile profile,
        CancellationToken cancellationToken);
}

public interface IMcpTransportHttpClientFactory
{
    HttpClient Create(McpRemoteServerProfile profile);
}
