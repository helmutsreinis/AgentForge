using AgentForge.Abstractions.Environments;
using AgentForge.Abstractions.Time;
using AgentForge.Domain.Environments;
using AgentForge.Domain.Primitives;
using AgentForge.Environment;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AgentForge.CrossPlatformTests;

public sealed class SystemEnvironmentProfilerTests
{
    [Fact]
    public async Task Live_capture_is_bounded_and_matches_the_current_platform()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AgentForge:EnvironmentInventory:MaximumPathDirectories"] = "64",
                ["AgentForge:EnvironmentInventory:MaximumFilesPerDirectory"] = "1024",
                ["AgentForge:EnvironmentInventory:MaximumExecutables"] = "1024",
            })
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton<IClock>(new FixedClock(new DateTimeOffset(2026, 8, 10, 18, 30, 0, TimeSpan.Zero)));
        services.AddAgentForgeEnvironment(configuration);
        await using var provider = services.BuildServiceProvider(validateScopes: true);
        await using var scope = provider.CreateAsyncScope();

        var result = await scope.ServiceProvider.GetRequiredService<IEnvironmentProfiler>()
            .CaptureAsync(
                new CaptureEnvironmentRequest(new ActorId("cross-platform"), new CorrelationId("live-inventory")),
                CancellationToken.None);

        Assert.True(result.IsSuccess, result.Failure?.Message);
        Assert.StartsWith("sha256:", result.Value.Fingerprint, StringComparison.Ordinal);
        Assert.Equal(71, result.Value.Fingerprint.Length);
        Assert.InRange(result.Value.Executables.Count, 0, 1024);
        Assert.All(result.Value.Executables, item => Assert.Equal("PATH", item.Provenance));
        Assert.DoesNotContain(
            result.Value.Executables,
            item => item.FullPath.Contains('=') || string.IsNullOrWhiteSpace(item.Name));

        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(HostOperatingSystem.Windows, result.Value.OperatingSystem.Family);
            Assert.Null(result.Value.OperatingSystem.Distribution);
            Assert.Equal('\\', result.Value.FileSystem.DirectorySeparator);
        }
        else if (OperatingSystem.IsLinux())
        {
            Assert.Equal(HostOperatingSystem.Linux, result.Value.OperatingSystem.Family);
            Assert.NotNull(result.Value.OperatingSystem.Distribution);
            Assert.Equal('/', result.Value.FileSystem.DirectorySeparator);
            var expectedWsl = !string.IsNullOrWhiteSpace(
                global::System.Environment.GetEnvironmentVariable("WSL_DISTRO_NAME"));
            if (expectedWsl)
            {
                Assert.True(result.Value.Wsl.IsWsl);
            }
        }
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }
}
