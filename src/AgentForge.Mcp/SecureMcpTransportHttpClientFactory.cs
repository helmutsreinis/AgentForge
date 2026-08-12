using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using AgentForge.Abstractions.Mcp;
using AgentForge.Abstractions.Security;
using AgentForge.Domain.Mcp;

namespace AgentForge.Mcp;

internal sealed class SecureMcpTransportHttpClientFactory(ISecretStore secretStore)
    : IMcpTransportHttpClientFactory
{
    public HttpClient Create(McpRemoteServerProfile profile)
    {
        var sockets = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1),
            UseCookies = false,
            ConnectCallback = (context, cancellationToken) => ConnectAsync(
                context.DnsEndPoint, profile.NetworkScope, cancellationToken),
        };
        HttpMessageHandler handler = sockets;
        if (profile.BearerCredentialReference is not null)
            handler = new InvocationBearerHandler(secretStore, profile.BearerCredentialReference) { InnerHandler = sockets };
        return new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
    }

    private static async ValueTask<Stream> ConnectAsync(
        DnsEndPoint endpoint,
        McpRemoteNetworkScope scope,
        CancellationToken cancellationToken)
    {
        var addresses = await Dns.GetHostAddressesAsync(endpoint.Host, cancellationToken);
        if (addresses.Length == 0 || addresses.Any(address => scope == McpRemoteNetworkScope.Loopback
                ? !IPAddress.IsLoopback(address)
                : !McpRemoteProfileValidator.IsPublic(address)))
            throw new HttpRequestException("MCP destination resolved outside its approved network scope.");
        Exception? last = null;
        foreach (var address in addresses.OrderBy(address => address.AddressFamily))
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(address, endpoint.Port, cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception exception) when (exception is SocketException or OperationCanceledException)
            {
                socket.Dispose();
                last = exception;
                if (exception is OperationCanceledException) throw;
            }
        }
        throw new HttpRequestException("MCP destination could not be reached.", last);
    }

    private sealed class InvocationBearerHandler(
        ISecretStore secretStore,
        AgentForge.Domain.Security.SecretReference reference) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var materialized = await secretStore.MaterializeAsync(reference, cancellationToken);
            if (!materialized.IsSuccess) throw new HttpRequestException("MCP bearer credential is unavailable.");
            using var lease = materialized.Value;
            var credential = new string(lease.Value.Span);
            try
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
                return await base.SendAsync(request, cancellationToken);
            }
            finally
            {
                request.Headers.Authorization = null;
            }
        }
    }
}
