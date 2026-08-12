using AgentForge.Domain.Mcp;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Security;

namespace AgentForge.UnitTests;

public sealed class McpRemoteProfileValidatorTests
{
    [Fact]
    public void Http_profiles_require_exact_scope_transport_and_credentials()
    {
        var privateEndpoint = Profile(
            new Uri("https://192.168.1.8/mcp"),
            McpRemoteNetworkScope.PublicHttps,
            new SecretReference("os", "mcp-token"));
        Assert.Equal(FailureCode.ValidationFailure,
            McpRemoteProfileValidator.Validate(privateEndpoint).Failure?.Code);

        var missingCredential = Profile(
            new Uri("https://mcp.example.test/mcp"),
            McpRemoteNetworkScope.PublicHttps,
            null);
        Assert.Equal(FailureCode.ValidationFailure,
            McpRemoteProfileValidator.Validate(missingCredential).Failure?.Code);

        var loopback = Profile(new Uri("http://127.0.0.1:8080/mcp"), McpRemoteNetworkScope.Loopback, null);
        Assert.True(McpRemoteProfileValidator.Validate(loopback).IsSuccess);
    }

    [Fact]
    public void Stdio_profiles_require_an_exact_existing_command_and_no_network_credential()
    {
        var command = System.Environment.ProcessPath!;
        var profile = new McpRemoteServerProfile(
            "local-stdio",
            McpRemoteTransport.Stdio,
            null,
            command,
            ["--version"],
            Directory.GetCurrentDirectory(),
            McpRemoteNetworkScope.Loopback,
            null,
            ["status"],
            Array.Empty<string>());
        Assert.True(McpRemoteProfileValidator.Validate(profile).IsSuccess);

        Assert.Equal(FailureCode.ValidationFailure, McpRemoteProfileValidator.Validate(profile with
        {
            BearerCredentialReference = new SecretReference("os", "forbidden"),
        }).Failure?.Code);
    }

    private static McpRemoteServerProfile Profile(
        Uri endpoint,
        McpRemoteNetworkScope scope,
        SecretReference? credential) => new(
            "remote-http",
            McpRemoteTransport.StreamableHttp,
            endpoint,
            null,
            Array.Empty<string>(),
            null,
            scope,
            credential,
            ["status"],
            ["agentforge://status"]);
}
