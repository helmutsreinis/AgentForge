using System.Text.Json;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Security;

namespace AgentForge.Domain.Mcp;

public enum McpRemoteTransport
{
    Stdio,
    StreamableHttp,
}

public enum McpRemoteNetworkScope
{
    Loopback,
    PublicHttps,
}

public sealed record McpRemoteServerProfile(
    string Name,
    McpRemoteTransport Transport,
    Uri? Endpoint,
    string? Command,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory,
    McpRemoteNetworkScope NetworkScope,
    SecretReference? BearerCredentialReference,
    IReadOnlyList<string> AllowedTools,
    IReadOnlyList<string> AllowedResources,
    int MaximumResultCharacters = 65_536);

public sealed record McpRemoteTool(string Name, string? Description, string InputSchemaJson);

public sealed record McpRemoteResource(string Uri, string Name, string? Description, string? MediaType);

public sealed record McpRemoteResult(string Json, bool IsError);

public static class McpRemoteProfileValidator
{
    public static DomainResult<bool> Validate(McpRemoteServerProfile? profile)
    {
        if (profile is null || !IsText(profile.Name, 128) || !Enum.IsDefined(profile.Transport) ||
            !Enum.IsDefined(profile.NetworkScope) || profile.Arguments is null || profile.Arguments.Count > 128 ||
            profile.Arguments.Any(argument => !IsArgument(argument)) ||
            !IsList(profile.AllowedTools) || !IsList(profile.AllowedResources) ||
            profile.MaximumResultCharacters is < 256 or > 1_048_576)
            return Invalid("MCP remote profile is invalid or exceeds a security bound.");
        if (profile.Transport == McpRemoteTransport.Stdio)
        {
            if (profile.Endpoint is not null || profile.NetworkScope != McpRemoteNetworkScope.Loopback ||
                profile.BearerCredentialReference is not null || string.IsNullOrWhiteSpace(profile.Command) ||
                !Path.IsPathFullyQualified(profile.Command) || !File.Exists(profile.Command) ||
                !IsLinkFreePath(profile.Command, file: true) ||
                string.IsNullOrWhiteSpace(profile.WorkingDirectory) ||
                !Path.IsPathFullyQualified(profile.WorkingDirectory) || !Directory.Exists(profile.WorkingDirectory) ||
                !IsLinkFreePath(profile.WorkingDirectory, file: false))
                return Invalid("MCP stdio requires an exact local command and working directory with no network credential.");
        }
        else
        {
            if (profile.Command is not null || profile.WorkingDirectory is not null || profile.Arguments.Count != 0 ||
                profile.Endpoint is null || !IsHttpEndpoint(profile.Endpoint, profile.NetworkScope) ||
                profile.NetworkScope == McpRemoteNetworkScope.PublicHttps && profile.BearerCredentialReference is null)
                return Invalid("MCP HTTP endpoint, network scope, transport, or credential policy is invalid.");
        }
        return DomainResult.Success(true);
    }

    private static bool IsLinkFreePath(string path, bool file)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            if (file && (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0) return false;
            DirectoryInfo? directory = file ? Directory.GetParent(fullPath) : new DirectoryInfo(fullPath);
            while (directory is not null)
            {
                if ((directory.Attributes & FileAttributes.ReparsePoint) != 0 || directory.LinkTarget is not null)
                    return false;
                directory = directory.Parent;
            }
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            System.Security.SecurityException)
        {
            return false;
        }
    }

    private static bool IsHttpEndpoint(Uri endpoint, McpRemoteNetworkScope scope)
    {
        if (!endpoint.IsAbsoluteUri || !string.IsNullOrEmpty(endpoint.UserInfo) ||
            !string.IsNullOrEmpty(endpoint.Query) || !string.IsNullOrEmpty(endpoint.Fragment)) return false;
        return scope switch
        {
            McpRemoteNetworkScope.Loopback => endpoint.Scheme is "http" or "https" &&
                (string.Equals(endpoint.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
                 System.Net.IPAddress.TryParse(endpoint.Host, out var address) && System.Net.IPAddress.IsLoopback(address)),
            McpRemoteNetworkScope.PublicHttps => endpoint.Scheme == Uri.UriSchemeHttps &&
                !string.Equals(endpoint.Host, "localhost", StringComparison.OrdinalIgnoreCase) &&
                (!System.Net.IPAddress.TryParse(endpoint.Host, out var address) || IsPublic(address)),
            _ => false,
        };
    }

    public static bool IsPublic(System.Net.IPAddress address)
    {
        if (System.Net.IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal ||
            address.IsIPv6Multicast) return false;
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            var ipv6 = address.GetAddressBytes();
            return !address.Equals(System.Net.IPAddress.IPv6Any) && (ipv6[0] & 0xfe) != 0xfc &&
                !(ipv6[0] == 0x20 && ipv6[1] == 0x01 && ipv6[2] == 0x0d && ipv6[3] == 0xb8);
        }
        var bytes = address.GetAddressBytes();
        return bytes[0] != 0 && bytes[0] != 10 && bytes[0] != 127 && bytes[0] < 224 &&
            !(bytes[0] == 169 && bytes[1] == 254) && !(bytes[0] == 172 && bytes[1] is >= 16 and <= 31) &&
            !(bytes[0] == 192 && bytes[1] == 168) && !(bytes[0] == 100 && bytes[1] is >= 64 and <= 127) &&
            !(bytes[0] == 198 && bytes[1] is 18 or 19);
    }

    private static bool IsList(IReadOnlyList<string> values) => values.Count <= 128 &&
        values.All(value => IsText(value, 256)) && values.Distinct(StringComparer.Ordinal).Count() == values.Count;

    private static bool IsArgument(string value) => value.Length <= 4096 && !value.Any(char.IsControl);

    private static bool IsText(string value, int maximum) => !string.IsNullOrWhiteSpace(value) &&
        value.Length <= maximum && !value.Any(char.IsControl);

    private static DomainResult<bool> Invalid(string message) =>
        DomainResult.Fail<bool>(new DomainFailure(FailureCode.ValidationFailure, message));
}
