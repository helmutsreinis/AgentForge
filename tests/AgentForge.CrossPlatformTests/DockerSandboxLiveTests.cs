using AgentForge.Abstractions.Time;
using AgentForge.Abstractions.Tools;
using AgentForge.Domain.Tools;
using AgentForge.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AgentForge.CrossPlatformTests;

public sealed class DockerSandboxLiveTests : IDisposable
{
    private const ProcessIsolationFeature Required =
        ProcessIsolationFeature.DirectExecutable | ProcessIsolationFeature.ArgumentArray |
        ProcessIsolationFeature.EnvironmentAllowlist | ProcessIsolationFeature.WorkingDirectoryContainment |
        ProcessIsolationFeature.BoundedOutput | ProcessIsolationFeature.WallClockTimeout |
        ProcessIsolationFeature.ProcessTreeTermination | ProcessIsolationFeature.NetworkIsolation |
        ProcessIsolationFeature.FileSystemIsolation | ProcessIsolationFeature.CpuLimit |
        ProcessIsolationFeature.MemoryLimit | ProcessIsolationFeature.ProcessLimit;
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"agentforge-docker-live-{Guid.NewGuid():N}");

    [DockerSandboxLiveFact]
    public async Task Configured_digest_pinned_image_runs_with_declared_isolation()
    {
        var runtime = global::System.Environment.GetEnvironmentVariable("AGENTFORGE_TEST_DOCKER_RUNTIME")!;
        var image = global::System.Environment.GetEnvironmentVariable("AGENTFORGE_TEST_DOCKER_IMAGE")!;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AgentForge:DockerSandbox:RuntimeExecutable"] = runtime,
            ["AgentForge:DockerSandbox:ImageReference"] = image,
            ["AgentForge:DockerSandbox:ContainerUser"] = "1654:1654",
            ["AgentForge:DockerSandbox:MemoryMegabytes"] = "256",
            ["AgentForge:DockerSandbox:CpuLimit"] = "0.5",
            ["AgentForge:DockerSandbox:ProcessLimit"] = "64",
        }).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IClock, LiveClock>();
        services.AddAgentForgeTools(configuration);
        await using var provider = services.BuildServiceProvider(validateScopes: true);
        var workspace = Directory.CreateDirectory(_root).FullName;
        var executable = global::System.Environment.ProcessPath
            ?? throw new InvalidOperationException("Current dotnet host path is unavailable.");

        var result = await provider.GetRequiredService<ISandbox>().ExecuteAsync(new ProcessExecutionRequest(
            executable,
            ["--info"],
            workspace,
            workspace,
            new Dictionary<string, string>(),
            TimeSpan.FromSeconds(60),
            262_144,
            ProcessNetworkPolicy.Denied,
            ProcessSandboxKind.Container,
            Required,
            ProcessFileSystemPolicy.ReadOnlyWorkspace), null, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Failure?.Message);
        Assert.Equal(0, result.Value.ExitCode);
        Assert.Equal(ProcessSandboxKind.Container, result.Value.Sandbox.Kind);
        Assert.Equal(Required, result.Value.Sandbox.SupportedFeatures);
        Assert.Contains(".NET SDK", System.Text.Encoding.UTF8.GetString(result.Value.StandardOutput),
            StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class LiveClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }
}

internal sealed class DockerSandboxLiveFactAttribute : FactAttribute
{
    public DockerSandboxLiveFactAttribute()
    {
        var runtime = global::System.Environment.GetEnvironmentVariable("AGENTFORGE_TEST_DOCKER_RUNTIME");
        var image = global::System.Environment.GetEnvironmentVariable("AGENTFORGE_TEST_DOCKER_IMAGE");
        if (string.IsNullOrWhiteSpace(runtime) || !Path.IsPathFullyQualified(runtime) || !File.Exists(runtime) ||
            string.IsNullOrWhiteSpace(image))
            Skip = "Set exact Docker runtime and digest-pinned sandbox image variables to run this equipped gate.";
    }
}
