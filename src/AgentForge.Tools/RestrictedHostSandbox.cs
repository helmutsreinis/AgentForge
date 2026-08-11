using System.Buffers;
using System.ComponentModel;
using System.Diagnostics;
using AgentForge.Abstractions.Time;
using AgentForge.Abstractions.Tools;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Tools;
using Microsoft.Extensions.Options;

namespace AgentForge.Tools;

internal sealed class RestrictedHostSandbox(
    IClock clock,
    IOptions<RestrictedProcessOptions> options) : ISandbox
{
    private const ProcessIsolationFeature BaseFeatures =
        ProcessIsolationFeature.DirectExecutable |
        ProcessIsolationFeature.ArgumentArray |
        ProcessIsolationFeature.EnvironmentAllowlist |
        ProcessIsolationFeature.WorkingDirectoryContainment |
        ProcessIsolationFeature.BoundedOutput |
        ProcessIsolationFeature.WallClockTimeout |
        ProcessIsolationFeature.ProcessTreeTermination;

    private readonly RestrictedProcessOptions _options = options.Value;

    public ProcessSandboxCapabilities Capabilities { get; } = new(
        ProcessSandboxKind.RestrictedHost,
        true,
        BaseFeatures | (OperatingSystem.IsWindows()
            ? ProcessIsolationFeature.KillOnControllerExit
            : ProcessIsolationFeature.None),
        OperatingSystem.IsWindows()
            ? "Direct process with Windows Job Object kill-on-close and bounded host controls."
            : "Direct process with managed process-tree termination and bounded host controls.");

    public async Task<DomainResult<ProcessExecutionResult>> ExecuteAsync(
        ProcessExecutionRequest request,
        IProcessOutputObserver? observer,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var validation = Validate(request);
        if (!validation.IsSuccess)
        {
            return DomainResult.Fail<ProcessExecutionResult>(validation.Failure!);
        }

        var workingDirectory = WorkspacePathGuard.Resolve(request.WorkspaceRoot, request.WorkingDirectory);
        if (!workingDirectory.IsSuccess)
        {
            return DomainResult.Fail<ProcessExecutionResult>(workingDirectory.Failure!);
        }

        var executable = ResolveExecutable(request.ExecutablePath);
        if (!executable.IsSuccess)
        {
            return DomainResult.Fail<ProcessExecutionResult>(executable.Failure!);
        }

        var environment = BuildEnvironment(request.Environment);
        if (!environment.IsSuccess)
        {
            return DomainResult.Fail<ProcessExecutionResult>(environment.Failure!);
        }

        IProcessTreeController controller;
        try
        {
            controller = ProcessTreeController.Create();
        }
        catch (Exception exception) when (exception is Win32Exception or PlatformNotSupportedException)
        {
            return Unsupported("Required process-tree containment is unavailable on this host.");
        }

        using (controller)
        using (var process = new Process
        {
            StartInfo = CreateStartInfo(
                executable.Value,
                request.Arguments,
                workingDirectory.Value.WorkingDirectory,
                environment.Value),
        })
        {
            var startedAt = clock.UtcNow;
            var stopwatch = Stopwatch.StartNew();
            try
            {
                if (!process.Start())
                {
                    return ExternalFailure("Restricted process could not be started.");
                }
            }
            catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or IOException)
            {
                return ExternalFailure("Restricted process could not be started.");
            }

            if (!controller.Attach(process))
            {
                controller.Terminate(process);
                await DrainAfterTerminationAsync(process, [], _options.TerminationWaitSeconds);
                return Unsupported("Required process-tree containment could not be attached.");
            }

            process.StandardInput.Close();
            using var output = new BoundedProcessOutput(request.MaximumOutputBytes);
            using var outputCancellation = new CancellationTokenSource();
            var observerFailure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var standardOutput = PumpAsync(
                process.StandardOutput.BaseStream,
                ProcessOutputChannel.StandardOutput,
                output,
                observer,
                observerFailure,
                outputCancellation.Token);
            var standardError = PumpAsync(
                process.StandardError.BaseStream,
                ProcessOutputChannel.StandardError,
                output,
                observer,
                observerFailure,
                outputCancellation.Token);
            Task[] pumps = [standardOutput, standardError];
            var exit = process.WaitForExitAsync(CancellationToken.None);
            var processAndOutput = Task.WhenAll([exit, .. pumps]);
            var timeout = Task.Delay(request.Timeout, CancellationToken.None);
            var canceled = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            var completed = await Task.WhenAny(
                processAndOutput,
                timeout,
                canceled,
                output.LimitExceeded,
                observerFailure.Task);

            if (completed == canceled || cancellationToken.IsCancellationRequested)
            {
                outputCancellation.Cancel();
                controller.Terminate(process);
                await DrainAfterTerminationAsync(process, pumps, _options.TerminationWaitSeconds);
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (completed == output.LimitExceeded || output.LimitExceeded.IsCompleted)
            {
                outputCancellation.Cancel();
                controller.Terminate(process);
                await DrainAfterTerminationAsync(process, pumps, _options.TerminationWaitSeconds);
                return BudgetFailure("Restricted process exceeded its combined output limit.");
            }

            if (completed == timeout)
            {
                outputCancellation.Cancel();
                controller.Terminate(process);
                await DrainAfterTerminationAsync(process, pumps, _options.TerminationWaitSeconds);
                return BudgetFailure("Restricted process exceeded its wall-clock timeout.");
            }

            if (completed == observerFailure.Task)
            {
                outputCancellation.Cancel();
                controller.Terminate(process);
                await DrainAfterTerminationAsync(process, pumps, _options.TerminationWaitSeconds);
                return ExternalFailure("Restricted process output observer failed.");
            }

            await processAndOutput;
            if (output.LimitExceeded.IsCompleted)
            {
                controller.Terminate(process);
                return BudgetFailure("Restricted process exceeded its combined output limit.");
            }

            stopwatch.Stop();
            var captured = output.Snapshot();
            return DomainResult.Success(new ProcessExecutionResult(
                process.ExitCode,
                captured.StandardOutput,
                captured.StandardError,
                startedAt,
                clock.UtcNow,
                stopwatch.Elapsed,
                Capabilities));
        }
    }

    private DomainResult<bool> Validate(ProcessExecutionRequest request)
    {
        if (!Enum.IsDefined(request.NetworkPolicy) || !Enum.IsDefined(request.RequiredSandbox) ||
            request.RequiredSandbox is not ProcessSandboxKind.RestrictedHost)
        {
            return DomainResult.Fail<bool>(new DomainFailure(
                FailureCode.UnsupportedCapability,
                "Requested sandbox kind is unavailable."));
        }

        if (request.NetworkPolicy is not ProcessNetworkPolicy.InheritHost)
        {
            return DomainResult.Fail<bool>(new DomainFailure(
                FailureCode.UnsupportedCapability,
                "Restricted host execution cannot enforce the requested network isolation."));
        }

        if ((request.RequiredFeatures & ~Capabilities.SupportedFeatures) != 0)
        {
            return DomainResult.Fail<bool>(new DomainFailure(
                FailureCode.UnsupportedCapability,
                "Restricted host execution cannot enforce every required isolation feature."));
        }

        if (request.Arguments is null || request.Arguments.Count > _options.MaximumArguments ||
            request.Arguments.Any(item => item is null || item.Contains('\0')) ||
            request.Arguments.Sum(item => (long)item.Length) > _options.MaximumArgumentCharacters ||
            request.Environment is null || request.Environment.Count > _options.MaximumEnvironmentVariables ||
            request.Timeout <= TimeSpan.Zero || request.Timeout > TimeSpan.FromSeconds(_options.MaximumTimeoutSeconds) ||
            request.MaximumOutputBytes <= 0 || request.MaximumOutputBytes > _options.MaximumOutputBytes)
        {
            return DomainResult.Fail<bool>(new DomainFailure(
                FailureCode.ValidationFailure,
                "Restricted process arguments, environment, timeout, or output bound is invalid."));
        }

        return DomainResult.Success(true);
    }

    private static DomainResult<string> ResolveExecutable(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || executablePath.Length > 2048)
        {
            return InvalidExecutable();
        }

        try
        {
            if (!Path.IsPathFullyQualified(executablePath))
            {
                return InvalidExecutable();
            }

            var normalized = Path.GetFullPath(executablePath);
            var file = new FileInfo(normalized);
            if (!file.Exists || file.LinkTarget is not null ||
                (file.Attributes & FileAttributes.ReparsePoint) != 0 || file.DirectoryName is null ||
                WorkspacePathGuard.ContainsLinkOrReparsePoint(file.DirectoryName))
            {
                return InvalidExecutable();
            }

            return DomainResult.Success(normalized);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or
            NotSupportedException or PathTooLongException or UnauthorizedAccessException)
        {
            return InvalidExecutable();
        }
    }

    private DomainResult<IReadOnlyDictionary<string, string>> BuildEnvironment(
        IReadOnlyDictionary<string, string> requested)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var inherited = new HashSet<string>(_options.AllowedInheritedEnvironmentVariables, comparer);
        var invocation = new HashSet<string>(_options.AllowedInvocationEnvironmentVariables, comparer);
        var result = new Dictionary<string, string>(comparer);
        foreach (var name in inherited)
        {
            var value = global::System.Environment.GetEnvironmentVariable(name);
            if (value is not null)
            {
                result[name] = value;
            }
        }

        foreach (var item in requested)
        {
            if (!invocation.Contains(item.Key) || !IsEnvironmentName(item.Key) || item.Value is null ||
                item.Value.Length > _options.MaximumEnvironmentValueCharacters || item.Value.Contains('\0'))
            {
                return DomainResult.Fail<IReadOnlyDictionary<string, string>>(new DomainFailure(
                    FailureCode.PolicyDenied,
                    "Invocation environment contains a variable that is not allowlisted or bounded."));
            }

            result[item.Key] = item.Value;
        }

        return DomainResult.Success<IReadOnlyDictionary<string, string>>(result);
    }

    private static ProcessStartInfo CreateStartInfo(
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string> environment)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        startInfo.ArgumentList.Clear();
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment.Clear();
        foreach (var item in environment)
        {
            startInfo.Environment[item.Key] = item.Value;
        }

        return startInfo;
    }

    private static async Task PumpAsync(
        Stream source,
        ProcessOutputChannel channel,
        BoundedProcessOutput output,
        IProcessOutputObserver? observer,
        TaskCompletionSource observerFailure,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(4096);
        try
        {
            while (true)
            {
                var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (read == 0)
                {
                    return;
                }

                try
                {
                    await output.AppendAsync(
                        channel,
                        buffer.AsMemory(0, read),
                        observer,
                        cancellationToken);
                }
                catch (Exception) when (!cancellationToken.IsCancellationRequested)
                {
                    observerFailure.TrySetResult();
                    return;
                }

                if (output.LimitExceeded.IsCompleted)
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The controlling execution path requested bounded observer and stream shutdown.
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException)
        {
            observerFailure.TrySetResult();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private static async Task DrainAfterTerminationAsync(
        Process process,
        IReadOnlyList<Task> pumps,
        int terminationWaitSeconds)
    {
        try
        {
            await Task.WhenAll([process.WaitForExitAsync(CancellationToken.None), .. pumps])
                .WaitAsync(TimeSpan.FromSeconds(terminationWaitSeconds));
        }
        catch (Exception exception) when (exception is TimeoutException or OperationCanceledException or
            InvalidOperationException or IOException)
        {
            // Controller disposal provides the final kill-on-close boundary where supported.
        }
    }

    private static bool IsEnvironmentName(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 128 &&
        (char.IsAsciiLetter(value[0]) || value[0] == '_') &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character == '_');

    private static DomainResult<string> InvalidExecutable() =>
        DomainResult.Fail<string>(new DomainFailure(
            FailureCode.PolicyDenied,
            "Executable must be an existing fully qualified non-link file."));

    private static DomainResult<ProcessExecutionResult> Unsupported(string message) =>
        DomainResult.Fail<ProcessExecutionResult>(new DomainFailure(FailureCode.UnsupportedCapability, message));

    private static DomainResult<ProcessExecutionResult> BudgetFailure(string message) =>
        DomainResult.Fail<ProcessExecutionResult>(new DomainFailure(FailureCode.BudgetExceeded, message));

    private static DomainResult<ProcessExecutionResult> ExternalFailure(string message) =>
        DomainResult.Fail<ProcessExecutionResult>(new DomainFailure(
            FailureCode.RecoverableExternalFailure,
            message,
            IsRetryable: true));
}
