using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AgentForge.Abstractions.Time;
using AgentForge.Abstractions.Tools;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Tools;
using AgentForge.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AgentForge.CrossPlatformTests;

public sealed class RestrictedHostSandboxTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 11, 8, 30, 0, TimeSpan.Zero);
    private readonly string _temporaryRoot = Path.Combine(
        Path.GetFullPath(Path.GetTempPath()),
        $"agentforge-process-{Guid.NewGuid():N}");
    private readonly ServiceProvider _services;

    public RestrictedHostSandboxTests()
    {
        Directory.CreateDirectory(_temporaryRoot);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AgentForge:RestrictedProcess:MaximumTimeoutSeconds"] = "5",
                ["AgentForge:RestrictedProcess:MaximumOutputBytes"] = "16384",
                ["AgentForge:RestrictedProcess:TerminationWaitSeconds"] = "5",
                ["AgentForge:RestrictedProcess:AllowedInvocationEnvironmentVariables:0"] = "AF_TEST_VALUE",
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IClock>(new FixedClock(Now));
        services.AddAgentForgeTools(configuration);
        _services = services.BuildServiceProvider(validateScopes: true);
    }

    [Fact]
    public async Task Arguments_are_not_interpreted_by_a_shell_and_streaming_matches_capture()
    {
        var sentinel = Path.Combine(_temporaryRoot, "injection-sentinel");
        using var observer = new RecordingObserver();
        var hostileArgument = $"; touch {sentinel} && echo injected";
        var result = await Sandbox.ExecuteAsync(
            Request("echo-arguments", hostileArgument, "spaces and \"quotes\""),
            observer,
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Failure?.Message);
        Assert.Equal(0, result.Value.ExitCode);
        Assert.False(File.Exists(sentinel));
        var parsed = JsonSerializer.Deserialize<string[]>(Encoding.UTF8.GetString(result.Value.StandardOutput));
        Assert.NotNull(parsed);
        Assert.Equal([hostileArgument, "spaces and \"quotes\""], parsed);
        Assert.Equal(result.Value.StandardOutput, observer.StandardOutput);
        Assert.True(result.Value.Sandbox.SupportedFeatures.HasFlag(ProcessIsolationFeature.ArgumentArray));
    }

    [Fact]
    public async Task Environment_is_cleared_and_only_invocation_allowlist_is_applied()
    {
        var result = await Sandbox.ExecuteAsync(
            Request("print-environment", "AF_TEST_VALUE", "PATH") with
            {
                Environment = new Dictionary<string, string> { ["AF_TEST_VALUE"] = "visible" },
            },
            null,
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Failure?.Message);
        using var document = JsonDocument.Parse(result.Value.StandardOutput);
        Assert.Equal("visible", document.RootElement.GetProperty("AF_TEST_VALUE").GetString());
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("PATH").ValueKind);

        var rejected = await Sandbox.ExecuteAsync(
            Request("print-environment", "NOT_ALLOWED") with
            {
                Environment = new Dictionary<string, string> { ["NOT_ALLOWED"] = "hidden" },
            },
            null,
            CancellationToken.None);
        Assert.Equal(FailureCode.PolicyDenied, rejected.Failure?.Code);
    }

    [Fact]
    public async Task Output_flood_fails_with_typed_budget_result()
    {
        var result = await Sandbox.ExecuteAsync(
            Request("flood", "20000", "20000") with { MaximumOutputBytes = 4096 },
            null,
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(FailureCode.BudgetExceeded, result.Failure?.Code);
    }

    [Fact]
    public async Task Blocking_output_observer_is_canceled_at_the_execution_timeout()
    {
        var observer = new BlockingObserver();
        var result = await Sandbox.ExecuteAsync(
            Request("echo-arguments", "observer-output") with { Timeout = TimeSpan.FromSeconds(3) },
            observer,
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(FailureCode.BudgetExceeded, result.Failure?.Code);
        Assert.True(await observer.Canceled.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task Timeout_terminates_the_process_tree()
    {
        var sentinel = Path.Combine(_temporaryRoot, "timeout-child-sentinel");
        using var observer = new RecordingObserver();
        var result = await Sandbox.ExecuteAsync(
            Request("spawn-child", sentinel, "30000") with { Timeout = TimeSpan.FromSeconds(2) },
            observer,
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(FailureCode.BudgetExceeded, result.Failure?.Code);
        Assert.True(TryReadProcessId(observer.StandardOutput, out var childId));
        Assert.True(await WaitForProcessExitAsync(childId));
        Assert.False(File.Exists(sentinel));
    }

    [Fact]
    public async Task Windows_job_attach_recaptures_a_preexisting_descendant()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var sentinel = Path.Combine(_temporaryRoot, "preexisting-child-sentinel");
        var startInfo = new ProcessStartInfo
        {
            FileName = DotnetHost,
            WorkingDirectory = _temporaryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in new[] { FixtureAssembly, "spawn-child", sentinel, "30000" })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var parent = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the pre-attachment process fixture.");
        using var readTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var childIdText = await parent.StandardOutput.ReadLineAsync(readTimeout.Token);
        Assert.True(int.TryParse(
            childIdText,
            System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture,
            out var childId));

        using var controller = ProcessTreeController.Create();
        var attached = controller.Attach(parent);
        controller.Terminate(parent);
        await parent.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(attached);
        Assert.True(await WaitForProcessExitAsync(childId));
        Assert.False(File.Exists(sentinel));
    }

    [Fact]
    public async Task Cancellation_terminates_the_process_tree_and_propagates()
    {
        var sentinel = Path.Combine(_temporaryRoot, "canceled-child-sentinel");
        using var observer = new RecordingObserver();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Sandbox.ExecuteAsync(
            Request("spawn-child", sentinel, "30000"),
            observer,
            cancellation.Token));

        Assert.True(TryReadProcessId(observer.StandardOutput, out var childId));
        Assert.True(await WaitForProcessExitAsync(childId));
        Assert.False(File.Exists(sentinel));
    }

    [Fact]
    public async Task Outside_or_linked_working_directory_is_rejected_before_start()
    {
        var outside = Path.Combine(Path.GetTempPath(), $"agentforge-outside-{Guid.NewGuid():N}");
        var sentinel = Path.Combine(outside, "outside-sentinel");
        Directory.CreateDirectory(outside);
        try
        {
            var outsideResult = await Sandbox.ExecuteAsync(
                Request("write-file", sentinel, "written") with { WorkingDirectory = outside },
                null,
                CancellationToken.None);
            Assert.Equal(FailureCode.PolicyDenied, outsideResult.Failure?.Code);
            Assert.False(File.Exists(sentinel));

            var link = Path.Combine(_temporaryRoot, "outside-link");
            if (TryCreateDirectoryLink(link, outside))
            {
                var linkResult = await Sandbox.ExecuteAsync(
                    Request("write-file", sentinel, "written") with { WorkingDirectory = link },
                    null,
                    CancellationToken.None);
                Assert.Equal(FailureCode.PolicyDenied, linkResult.Failure?.Code);
                Assert.False(File.Exists(sentinel));
            }
        }
        finally
        {
            if (Directory.Exists(outside))
            {
                Directory.Delete(outside, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Unsupported_network_or_container_isolation_never_starts_process()
    {
        var sentinel = Path.Combine(_temporaryRoot, "unsupported-sentinel");
        var network = await Sandbox.ExecuteAsync(
            Request("write-file", sentinel, "written") with { NetworkPolicy = ProcessNetworkPolicy.Denied },
            null,
            CancellationToken.None);
        var container = await Sandbox.ExecuteAsync(
            Request("write-file", sentinel, "written") with { RequiredSandbox = ProcessSandboxKind.Container },
            null,
            CancellationToken.None);

        Assert.Equal(FailureCode.UnsupportedCapability, network.Failure?.Code);
        Assert.Equal(FailureCode.UnsupportedCapability, container.Failure?.Code);
        Assert.False(File.Exists(sentinel));
    }

    [Fact]
    public async Task Non_executable_file_fails_without_shell_fallback()
    {
        var file = Path.Combine(_temporaryRoot, "not-an-executable");
        await File.WriteAllTextAsync(file, "plain text");
        var request = Request("exit", "0") with { ExecutablePath = file, Arguments = [] };

        var result = await Sandbox.ExecuteAsync(request, null, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(FailureCode.RecoverableExternalFailure, result.Failure?.Code);
    }

    public void Dispose()
    {
        _services.Dispose();
        var fullPath = Path.GetFullPath(_temporaryRoot);
        var temporary = Path.GetFullPath(Path.GetTempPath());
        if (fullPath.StartsWith(temporary, StringComparison.OrdinalIgnoreCase) &&
            Path.GetFileName(fullPath).StartsWith("agentforge-process-", StringComparison.Ordinal))
        {
            Directory.Delete(fullPath, recursive: true);
        }
    }

    private ISandbox Sandbox => _services.GetRequiredService<ISandbox>();

    private ProcessExecutionRequest Request(string command, params string[] arguments)
    {
        var allArguments = new List<string> { FixtureAssembly, command };
        allArguments.AddRange(arguments);
        return new ProcessExecutionRequest(
            DotnetHost,
            allArguments,
            _temporaryRoot,
            _temporaryRoot,
            new Dictionary<string, string>(),
            TimeSpan.FromSeconds(4),
            8192,
            ProcessNetworkPolicy.InheritHost,
            ProcessSandboxKind.RestrictedHost,
            ProcessIsolationFeature.ProcessTreeTermination);
    }

    private static string DotnetHost
    {
        get
        {
            var configured = global::System.Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
            if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
            {
                return Path.GetFullPath(configured);
            }

            var executableName = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
            var runtimeDirectory = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
            return Path.GetFullPath(Path.Combine(runtimeDirectory, "..", "..", "..", executableName));
        }
    }

    private static string FixtureAssembly
    {
        get
        {
            var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name
                ?? throw new InvalidOperationException("Could not determine test configuration.");
            return Path.Combine(
                FindRepositoryRoot(),
                "tests",
                "AgentForge.ProcessFixture",
                "bin",
                configuration,
                "net10.0",
                "AgentForge.ProcessFixture.dll");
        }
    }

    private static bool TryCreateDirectoryLink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    private static async Task<bool> WaitForProcessExitAsync(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
                return true;
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }

                return false;
            }
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    private static bool TryReadProcessId(byte[] output, out int processId) =>
        int.TryParse(
            Encoding.UTF8.GetString(output).Trim(),
            System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture,
            out processId);

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AgentForge.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Could not locate the repository root.");
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed class RecordingObserver : IProcessOutputObserver, IDisposable
    {
        private readonly MemoryStream _standardOutput = new();

        public byte[] StandardOutput => _standardOutput.ToArray();

        public ValueTask ObserveAsync(ProcessOutputChunk chunk, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (chunk.Channel is ProcessOutputChannel.StandardOutput)
            {
                _standardOutput.Write(chunk.Data);
            }

            return ValueTask.CompletedTask;
        }

        public void Dispose() => _standardOutput.Dispose();
    }

    private sealed class BlockingObserver : IProcessOutputObserver
    {
        private readonly TaskCompletionSource<bool> _canceled = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<bool> Canceled => _canceled.Task;

        public async ValueTask ObserveAsync(ProcessOutputChunk chunk, CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _canceled.TrySetResult(true);
                throw;
            }
        }
    }
}
