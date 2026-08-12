using AgentForge.Abstractions.Tools;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Tools;
using AgentForge.Tools;
using Microsoft.Extensions.Options;

namespace AgentForge.UnitTests;

public sealed class DockerContainerSandboxTests : IDisposable
{
    private const ProcessIsolationFeature Required =
        ProcessIsolationFeature.DirectExecutable | ProcessIsolationFeature.ArgumentArray |
        ProcessIsolationFeature.EnvironmentAllowlist | ProcessIsolationFeature.WorkingDirectoryContainment |
        ProcessIsolationFeature.BoundedOutput | ProcessIsolationFeature.WallClockTimeout |
        ProcessIsolationFeature.ProcessTreeTermination | ProcessIsolationFeature.NetworkIsolation |
        ProcessIsolationFeature.FileSystemIsolation | ProcessIsolationFeature.CpuLimit |
        ProcessIsolationFeature.MemoryLimit | ProcessIsolationFeature.ProcessLimit;
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"agentforge-docker-{Guid.NewGuid():N}");

    [Fact]
    public async Task Digest_pinned_container_uses_hardened_arguments_and_always_cleans_up()
    {
        var runtime = CreateFile("docker.exe");
        var executable = CreateFile("dotnet.exe");
        var workspace = Directory.CreateDirectory(Path.Combine(_root, "workspace")).FullName;
        var invoker = new RecordingRuntimeInvoker();
        var sandbox = CreateSandbox(runtime, invoker);

        var result = await sandbox.ExecuteAsync(Request(executable, workspace), null, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Failure?.Message);
        Assert.Equal(ProcessSandboxKind.Container, result.Value.Sandbox.Kind);
        Assert.Equal(Required, result.Value.Sandbox.SupportedFeatures);
        Assert.Equal(2, invoker.Requests.Count);
        var run = invoker.Requests[0];
        Assert.Equal(runtime, run.ExecutablePath);
        Assert.Equal(ProcessSandboxKind.RestrictedHost, run.RequiredSandbox);
        Assert.Equal(ProcessNetworkPolicy.InheritHost, run.NetworkPolicy);
        Assert.Empty(run.Environment);
        AssertArgumentPair(run.Arguments, "--network", "none");
        AssertArgumentPair(run.Arguments, "--cap-drop", "ALL");
        AssertArgumentPair(run.Arguments, "--security-opt", "no-new-privileges:true");
        AssertArgumentPair(run.Arguments, "--pids-limit", "64");
        AssertArgumentPair(run.Arguments, "--memory", "256m");
        AssertArgumentPair(run.Arguments, "--cpus", "0.5");
        AssertArgumentPair(run.Arguments, "--user", "65532:65532");
        Assert.Contains("--read-only", run.Arguments);
        Assert.Contains(run.Arguments, item => item.StartsWith("type=bind,src=", StringComparison.Ordinal) &&
            item.EndsWith(",dst=/workspace,readonly", StringComparison.Ordinal));
        Assert.Contains("agentforge/sandbox@sha256:" + new string('a', 64), run.Arguments);
        Assert.Contains("/usr/bin/dotnet", run.Arguments);
        Assert.Contains("$(hostile-input-is-data)", run.Arguments);
        Assert.Equal(["rm", "--force"], invoker.Requests[1].Arguments.Take(2));
        Assert.StartsWith("agentforge-", invoker.Requests[1].Arguments[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_runtime_and_unsafe_inputs_fail_typed_without_invocation()
    {
        var workspace = Directory.CreateDirectory(Path.Combine(_root, "workspace")).FullName;
        var executable = CreateFile("dotnet.exe");
        var invoker = new RecordingRuntimeInvoker();
        var unavailable = CreateSandbox(Path.Combine(_root, "missing-docker"), invoker);

        var missing = await unavailable.ExecuteAsync(Request(executable, workspace), null, CancellationToken.None);

        Assert.Equal(FailureCode.UnsupportedCapability, missing.Failure?.Code);
        Assert.Empty(invoker.Requests);

        var available = CreateSandbox(CreateFile("docker.exe"), invoker);
        var environment = await available.ExecuteAsync(Request(executable, workspace) with
        {
            Environment = new Dictionary<string, string> { ["SECRET"] = "must-not-enter-runtime-arguments" },
        }, null, CancellationToken.None);
        var unknown = await available.ExecuteAsync(Request(CreateFile("unknown.exe"), workspace), null,
            CancellationToken.None);

        Assert.Equal(FailureCode.PolicyDenied, environment.Failure?.Code);
        Assert.Equal(FailureCode.PolicyDenied, unknown.Failure?.Code);
        Assert.Empty(invoker.Requests);
    }

    private static DockerContainerSandbox CreateSandbox(string runtime, IContainerRuntimeInvoker invoker) => new(
        Options.Create(new DockerSandboxOptions
        {
            RuntimeExecutable = runtime,
            ImageReference = "agentforge/sandbox@sha256:" + new string('a', 64),
            MemoryMegabytes = 256,
            CpuLimit = 0.5m,
            ProcessLimit = 64,
            TemporaryMegabytes = 32,
        }),
        invoker);

    private static ProcessExecutionRequest Request(string executable, string workspace) => new(
        executable,
        ["test.dll", "$(hostile-input-is-data)"],
        workspace,
        workspace,
        new Dictionary<string, string>(),
        TimeSpan.FromSeconds(30),
        32_768,
        ProcessNetworkPolicy.Denied,
        ProcessSandboxKind.Container,
        Required,
        ProcessFileSystemPolicy.ReadOnlyWorkspace);

    private string CreateFile(string name)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, "fixture");
        return path;
    }

    private static void AssertArgumentPair(IReadOnlyList<string> arguments, string name, string value)
    {
        var index = arguments.ToList().IndexOf(name);
        Assert.InRange(index, 0, arguments.Count - 2);
        Assert.Equal(value, arguments[index + 1]);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class RecordingRuntimeInvoker : IContainerRuntimeInvoker
    {
        public List<ProcessExecutionRequest> Requests { get; } = [];

        public Task<DomainResult<ProcessExecutionResult>> InvokeAsync(
            ProcessExecutionRequest request,
            IProcessOutputObserver? observer,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var now = new DateTimeOffset(2026, 8, 12, 7, 0, 0, TimeSpan.Zero);
            return Task.FromResult(DomainResult.Success(new ProcessExecutionResult(
                0, "accepted"u8.ToArray(), [], now, now.AddMilliseconds(1), TimeSpan.FromMilliseconds(1),
                new ProcessSandboxCapabilities(ProcessSandboxKind.RestrictedHost, true,
                    ProcessIsolationFeature.DirectExecutable, "fixture"))));
        }
    }
}
