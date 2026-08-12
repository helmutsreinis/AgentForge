using AgentForge.Abstractions.Mcp;
using AgentForge.Domain.Primitives;
using Microsoft.AspNetCore.Http;

namespace AgentForge.Mcp;

internal sealed class McpCallerContext(IHttpContextAccessor httpContextAccessor) : IMcpCallerContext
{
    public McpTransportKind Transport => httpContextAccessor.HttpContext is null
        ? McpTransportKind.Stdio
        : McpTransportKind.StreamableHttp;

    public ActorId ActorId => httpContextAccessor.HttpContext?.Items[McpCallerContextItems.Actor] is ActorId actor
        ? actor
        : new ActorId("mcp:local-process");
}
