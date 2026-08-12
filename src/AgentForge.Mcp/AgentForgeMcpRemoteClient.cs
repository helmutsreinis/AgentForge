using System.Text.Json;
using AgentForge.Abstractions.Mcp;
using AgentForge.Domain.Mcp;
using AgentForge.Domain.Primitives;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Client;

namespace AgentForge.Mcp;

internal sealed class AgentForgeMcpRemoteClientFactory(
    IMcpTransportHttpClientFactory httpClientFactory,
    ILoggerFactory loggerFactory) : IAgentForgeMcpRemoteClientFactory
{
    public async Task<DomainResult<IAgentForgeMcpRemoteClient>> ConnectAsync(
        McpRemoteServerProfile profile,
        CancellationToken cancellationToken)
    {
        var validation = McpRemoteProfileValidator.Validate(profile);
        if (!validation.IsSuccess) return DomainResult.Fail<IAgentForgeMcpRemoteClient>(validation.Failure!);
        try
        {
            IClientTransport transport;
            if (profile.Transport == McpRemoteTransport.StreamableHttp)
            {
                var client = httpClientFactory.Create(profile);
                transport = new HttpClientTransport(new HttpClientTransportOptions
                {
                    Endpoint = profile.Endpoint!,
                    Name = profile.Name,
                    TransportMode = HttpTransportMode.StreamableHttp,
                    EnableStandaloneGetStream = false,
                    ConnectionTimeout = TimeSpan.FromSeconds(15),
                }, client, loggerFactory, ownsHttpClient: true);
            }
            else
            {
                transport = new StdioClientTransport(new StdioClientTransportOptions
                {
                    Command = profile.Command!,
                    Arguments = profile.Arguments.ToArray(),
                    Name = profile.Name,
                    WorkingDirectory = profile.WorkingDirectory,
                    InheritEnvironmentVariables = false,
                    EnvironmentVariables = new Dictionary<string, string?>(),
                    ShutdownTimeout = TimeSpan.FromSeconds(5),
                }, loggerFactory);
            }
            var clientSession = await McpClient.CreateAsync(transport, cancellationToken: cancellationToken);
            return DomainResult.Success<IAgentForgeMcpRemoteClient>(
                new AgentForgeMcpRemoteClient(profile, clientSession));
        }
        catch (Exception exception) when (exception is McpException or HttpRequestException or IOException or
            InvalidOperationException)
        {
            return DomainResult.Fail<IAgentForgeMcpRemoteClient>(new DomainFailure(
                FailureCode.RecoverableExternalFailure, "MCP remote connection failed.", true));
        }
    }
}

internal sealed class AgentForgeMcpRemoteClient(
    McpRemoteServerProfile profile,
    McpClient client) : IAgentForgeMcpRemoteClient
{
    public async Task<DomainResult<IReadOnlyList<McpRemoteTool>>> ListToolsAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            var tools = await client.ListToolsAsync(cancellationToken: cancellationToken);
            if (tools.Count > 4096) return Budget<IReadOnlyList<McpRemoteTool>>("MCP tool catalog exceeds its bound.");
            var allowed = tools.Where(tool => profile.AllowedTools.Contains(tool.Name, StringComparer.Ordinal))
                .Select(tool => new McpRemoteTool(
                    tool.Name,
                    Bound(tool.Description, 4096),
                    Bound(tool.JsonSchema.GetRawText(), 65_536)!))
                .OrderBy(tool => tool.Name, StringComparer.Ordinal)
                .ToArray();
            return DomainResult.Success<IReadOnlyList<McpRemoteTool>>(allowed);
        }
        catch (Exception exception) when (exception is McpException or HttpRequestException or IOException)
        {
            return External<IReadOnlyList<McpRemoteTool>>("MCP tool discovery failed.");
        }
    }

    public async Task<DomainResult<IReadOnlyList<McpRemoteResource>>> ListResourcesAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            var resources = await client.ListResourcesAsync(cancellationToken: cancellationToken);
            if (resources.Count > 4096)
                return Budget<IReadOnlyList<McpRemoteResource>>("MCP resource catalog exceeds its bound.");
            var allowed = resources.Where(resource =>
                    profile.AllowedResources.Contains(resource.Uri, StringComparer.Ordinal))
                .Select(resource => new McpRemoteResource(
                    resource.Uri, Bound(resource.Name, 512)!, Bound(resource.Description, 4096),
                    Bound(resource.MimeType, 256)))
                .OrderBy(resource => resource.Uri, StringComparer.Ordinal)
                .ToArray();
            return DomainResult.Success<IReadOnlyList<McpRemoteResource>>(allowed);
        }
        catch (Exception exception) when (exception is McpException or HttpRequestException or IOException)
        {
            return External<IReadOnlyList<McpRemoteResource>>("MCP resource discovery failed.");
        }
    }

    public async Task<DomainResult<McpRemoteResult>> CallToolAsync(
        string name,
        IReadOnlyDictionary<string, JsonElement> arguments,
        CancellationToken cancellationToken)
    {
        if (!profile.AllowedTools.Contains(name, StringComparer.Ordinal) || !IsArguments(arguments))
            return Denied("MCP tool invocation is absent from the exact profile allowlist or has invalid arguments.");
        try
        {
            var values = arguments.ToDictionary(item => item.Key, item => (object?)item.Value.Clone(), StringComparer.Ordinal);
            var result = await client.CallToolAsync(name, values, cancellationToken: cancellationToken);
            return BoundedResult(result.Content, result.IsError is true);
        }
        catch (Exception exception) when (exception is McpException or HttpRequestException or IOException)
        {
            return External<McpRemoteResult>("MCP tool invocation failed.");
        }
    }

    public async Task<DomainResult<McpRemoteResult>> ReadResourceAsync(
        string uri,
        CancellationToken cancellationToken)
    {
        if (!profile.AllowedResources.Contains(uri, StringComparer.Ordinal))
            return Denied("MCP resource is absent from the exact profile allowlist.");
        try
        {
            var result = await client.ReadResourceAsync(uri, cancellationToken: cancellationToken);
            return BoundedResult(result.Contents, false);
        }
        catch (Exception exception) when (exception is McpException or HttpRequestException or IOException)
        {
            return External<McpRemoteResult>("MCP resource read failed.");
        }
    }

    public ValueTask DisposeAsync() => client.DisposeAsync();

    private DomainResult<McpRemoteResult> BoundedResult(object value, bool isError)
    {
        var json = JsonSerializer.Serialize(value);
        return json.Length <= profile.MaximumResultCharacters
            ? DomainResult.Success(new McpRemoteResult(json, isError))
            : Budget<McpRemoteResult>("MCP result exceeds the configured character bound.");
    }

    private static bool IsArguments(IReadOnlyDictionary<string, JsonElement>? arguments)
    {
        if (arguments is null || arguments.Count > 128 || arguments.Any(item =>
                string.IsNullOrWhiteSpace(item.Key) || item.Key.Length > 256 || item.Key.Any(char.IsControl))) return false;
        return JsonSerializer.SerializeToUtf8Bytes(arguments).Length <= 262_144;
    }

    private static string? Bound(string? value, int maximum)
    {
        if (value is null) return null;
        var bounded = value[..Math.Min(value.Length, maximum)];
        return bounded.Any(char.IsControl)
            ? new string(bounded.Select(character => char.IsControl(character) ? ' ' : character).ToArray())
            : bounded;
    }

    private static DomainResult<McpRemoteResult> Denied(string message) =>
        DomainResult.Fail<McpRemoteResult>(new DomainFailure(FailureCode.PolicyDenied, message));

    private static DomainResult<T> Budget<T>(string message) =>
        DomainResult.Fail<T>(new DomainFailure(FailureCode.BudgetExceeded, message));

    private static DomainResult<T> External<T>(string message) =>
        DomainResult.Fail<T>(new DomainFailure(FailureCode.RecoverableExternalFailure, message, true));
}
