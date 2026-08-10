using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace AgentForge.EndToEndTests;

public sealed class CleanLaunchTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"agentforge-e2e-{Guid.NewGuid():N}");
    private readonly WebApplicationFactory<Program> _factory;

    public CleanLaunchTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["AgentForge:Installation:DataDirectory"] = _directory,
                    ["AgentForge:Host:Urls"] = string.Empty,
                });
            });
        });
    }

    [Fact]
    public async Task Clean_launch_exposes_setup_but_blocks_normal_operation()
    {
        using var client = _factory.CreateClient();

        using var setup = await client.GetAsync("/api/v1/setup/status", CancellationToken.None);
        using var runtime = await client.GetAsync("/api/v1/runtime/ping", CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, runtime.StatusCode);
    }

    public void Dispose()
    {
        _factory.Dispose();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
