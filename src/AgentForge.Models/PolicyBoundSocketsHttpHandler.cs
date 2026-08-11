using System.Net;
using System.Net.Sockets;
using AgentForge.Domain.Models;

namespace AgentForge.Models;

internal static class PolicyBoundSocketsHttpHandler
{
    public static SocketsHttpHandler Create(Uri endpoint, ModelProviderDataLocation location)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = 4,
            MaxResponseHeadersLength = 16,
            ConnectCallback = (context, cancellationToken) => ConnectAsync(
                context.DnsEndPoint,
                endpoint,
                location,
                cancellationToken),
        };
    }

    private static async ValueTask<Stream> ConnectAsync(
        DnsEndPoint destination,
        Uri endpoint,
        ModelProviderDataLocation location,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(destination.Host, endpoint.IdnHost, StringComparison.OrdinalIgnoreCase) ||
            destination.Port != endpoint.Port)
        {
            throw new HttpRequestException("The transport attempted an endpoint outside the approved destination.");
        }

        IPAddress[] addresses;
        if (IPAddress.TryParse(destination.Host, out var literal))
        {
            addresses = [literal];
        }
        else
        {
            addresses = await Dns.GetHostAddressesAsync(destination.Host, cancellationToken);
        }

        if (!EndpointDestinationPolicy.Allows(location, addresses))
        {
            throw new HttpRequestException("The resolved provider destination violates its network policy.");
        }

        Exception? lastFailure = null;
        foreach (var address in addresses.Distinct())
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true,
            };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, destination.Port), cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception exception) when (exception is SocketException or IOException)
            {
                lastFailure = exception;
                socket.Dispose();
            }
        }

        throw new HttpRequestException("No approved provider destination accepted the connection.", lastFailure);
    }
}
