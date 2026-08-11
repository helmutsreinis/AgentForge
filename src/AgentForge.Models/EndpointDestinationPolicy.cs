using System.Net;
using AgentForge.Domain.Models;

namespace AgentForge.Models;

internal static class EndpointDestinationPolicy
{
    public static bool Allows(
        ModelProviderDataLocation location,
        IReadOnlyList<IPAddress> addresses)
    {
        if (location is ModelProviderDataLocation.InProcess || addresses is null ||
            addresses.Count is < 1 or > 64)
        {
            return false;
        }

        var normalized = addresses
            .Where(address => address is not null)
            .Select(Normalize)
            .Distinct()
            .ToArray();
        if (normalized.Length == 0)
        {
            return false;
        }

        return location switch
        {
            ModelProviderDataLocation.Loopback => normalized.All(IsLoopback),
            ModelProviderDataLocation.PrivateNetwork => normalized.All(IsPrivate),
            ModelProviderDataLocation.Cloud => normalized.All(IsGlobal),
            _ => false,
        };
    }

    public static ModelProviderDataLocation Infer(Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (string.Equals(endpoint.IdnHost, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return ModelProviderDataLocation.Loopback;
        }

        if (!IPAddress.TryParse(endpoint.IdnHost, out var address))
        {
            return ModelProviderDataLocation.Cloud;
        }

        address = Normalize(address);
        return IsLoopback(address)
            ? ModelProviderDataLocation.Loopback
            : IsPrivate(address)
                ? ModelProviderDataLocation.PrivateNetwork
                : ModelProviderDataLocation.Cloud;
    }

    private static bool IsLoopback(IPAddress address) => IPAddress.IsLoopback(address);

    private static bool IsPrivate(IPAddress address)
    {
        if (address.AddressFamily is System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] == 10 ||
                bytes[0] == 172 && bytes[1] is >= 16 and <= 31 ||
                bytes[0] == 192 && bytes[1] == 168;
        }

        if (address.AddressFamily is System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            var bytes = address.GetAddressBytes();
            return (bytes[0] & 0xfe) == 0xfc;
        }

        return false;
    }

    private static bool IsGlobal(IPAddress address)
    {
        if (address.AddressFamily is System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] is >= 1 and <= 223 &&
                bytes[0] != 10 && bytes[0] != 127 &&
                !(bytes[0] == 100 && bytes[1] is >= 64 and <= 127) &&
                !(bytes[0] == 169 && bytes[1] == 254) &&
                !(bytes[0] == 172 && bytes[1] is >= 16 and <= 31) &&
                !(bytes[0] == 192 && bytes[1] == 0 && bytes[2] is 0 or 2) &&
                !(bytes[0] == 192 && bytes[1] == 168) &&
                !(bytes[0] == 198 && bytes[1] is 18 or 19 or 51) &&
                !(bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113);
        }

        if (address.AddressFamily is System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            var bytes = address.GetAddressBytes();
            return !address.Equals(IPAddress.IPv6None) && !address.Equals(IPAddress.IPv6Any) &&
                !IPAddress.IsLoopback(address) && !address.IsIPv6LinkLocal && !address.IsIPv6Multicast &&
                !address.IsIPv6SiteLocal && (bytes[0] & 0xfe) != 0xfc &&
                !(bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0d && bytes[3] == 0xb8);
        }

        return false;
    }

    private static IPAddress Normalize(IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
}
