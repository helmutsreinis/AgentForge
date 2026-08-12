using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using AgentForge.Abstractions.Tools;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Tools;
using Microsoft.Extensions.Options;

namespace AgentForge.Tools;

internal sealed partial class DockerContainerSandbox : IProcessSandboxAdapter
{
    private const ProcessIsolationFeature Features =
        ProcessIsolationFeature.DirectExecutable |
        ProcessIsolationFeature.ArgumentArray |
        ProcessIsolationFeature.EnvironmentAllowlist |
        ProcessIsolationFeature.WorkingDirectoryContainment |
        ProcessIsolationFeature.BoundedOutput |
        ProcessIsolationFeature.WallClockTimeout |
        ProcessIsolationFeature.ProcessTreeTermination |
        ProcessIsolationFeature.NetworkIsolation |
        ProcessIsolationFeature.FileSystemIsolation |
        ProcessIsolationFeature.CpuLimit |
        ProcessIsolationFeature.MemoryLimit |
        ProcessIsolationFeature.ProcessLimit;
    private readonly DockerSandboxOptions _options;
    private readonly IContainerRuntimeInvoker _runtime;

    public DockerContainerSandbox(
        IOptions<DockerSandboxOptions> options,
        IContainerRuntimeInvoker runtime)
    {
        _options = options.Value;
        _runtime = runtime;
        var available = IsConfigured(_options) && File.Exists(_options.RuntimeExecutable) &&
            new FileInfo(_options.RuntimeExecutable).LinkTarget is null;
        Capabilities = new ProcessSandboxCapabilities(
            ProcessSandboxKind.Container,
            available,
            available ? Features : ProcessIsolationFeature.None,
            available
                ? "Digest-pinned Docker process with denied network, isolated filesystem, non-root identity, and hard resource limits."
                : "Docker container isolation is not configured or its exact runtime path is unavailable.");
    }

    public ProcessSandboxCapabilities Capabilities { get; }

    public async Task<DomainResult<ProcessExecutionResult>> ExecuteAsync(
        ProcessExecutionRequest request,
        IProcessOutputObserver? observer,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var validation = Validate(request);
        if (!validation.IsSuccess) return DomainResult.Fail<ProcessExecutionResult>(validation.Failure!);
        var working = WorkspacePathGuard.Resolve(request.WorkspaceRoot, request.WorkingDirectory);
        if (!working.IsSuccess) return DomainResult.Fail<ProcessExecutionResult>(working.Failure!);
        var executable = ResolveContainerExecutable(request.ExecutablePath);
        if (!executable.IsSuccess) return DomainResult.Fail<ProcessExecutionResult>(executable.Failure!);
        if (working.Value.WorkspaceRoot.Contains(','))
            return Unsupported("Docker cannot safely encode this workspace mount path.");

        var relativeWorking = Path.GetRelativePath(working.Value.WorkspaceRoot, working.Value.WorkingDirectory);
        var containerWorking = relativeWorking == "."
            ? "/workspace"
            : "/workspace/" + relativeWorking.Replace('\\', '/');
        var name = "agentforge-" + RandomNumberGenerator.GetHexString(20, lowercase: true);
        var mountMode = request.FileSystemPolicy is ProcessFileSystemPolicy.ReadOnlyWorkspace ? "readonly" : "rw";
        var arguments = new List<string>
        {
            "run", "--name", name, "--rm", "--network", "none", "--read-only", "--cap-drop", "ALL",
            "--security-opt", "no-new-privileges:true", "--pids-limit", _options.ProcessLimit.ToString(CultureInfo.InvariantCulture),
            "--memory", $"{_options.MemoryMegabytes}m", "--cpus", _options.CpuLimit.ToString(CultureInfo.InvariantCulture),
            "--user", _options.ContainerUser,
            "--mount", $"type=bind,src={working.Value.WorkspaceRoot},dst=/workspace,{mountMode}",
            "--tmpfs", $"/tmp:rw,noexec,nosuid,nodev,size={_options.TemporaryMegabytes}m",
            "--workdir", containerWorking,
            _options.ImageReference,
            executable.Value,
        };
        arguments.AddRange(request.Arguments);
        var runtimeRequest = RuntimeRequest(arguments, working.Value.WorkspaceRoot, request.Timeout,
            request.MaximumOutputBytes);
        try
        {
            var result = await _runtime.InvokeAsync(runtimeRequest, observer, cancellationToken);
            return result.IsSuccess
                ? DomainResult.Success(result.Value with { Sandbox = Capabilities })
                : DomainResult.Fail<ProcessExecutionResult>(result.Failure!);
        }
        finally
        {
            await CleanupAsync(name, working.Value.WorkspaceRoot);
        }
    }

    private DomainResult<bool> Validate(ProcessExecutionRequest request)
    {
        if (!Capabilities.IsAvailable) return DomainResult.Fail<bool>(new DomainFailure(
            FailureCode.UnsupportedCapability, Capabilities.Evidence));
        if (request.RequiredSandbox is not ProcessSandboxKind.Container ||
            request.NetworkPolicy is not ProcessNetworkPolicy.Denied || !Enum.IsDefined(request.FileSystemPolicy))
            return UnsupportedBoolean("Docker sandbox supports only explicitly denied networking.");
        if ((request.RequiredFeatures & ~Capabilities.SupportedFeatures) != 0)
            return UnsupportedBoolean("Docker sandbox cannot enforce every requested isolation feature.");
        if (request.Environment is null || request.Environment.Count != 0)
            return DomainResult.Fail<bool>(new DomainFailure(
                FailureCode.PolicyDenied, "Container environment materialization is disabled to keep secrets out of runtime arguments."));
        if (request.Arguments is null || request.Arguments.Count > 256 ||
            request.Arguments.Any(item => item is null || item.Contains('\0')) ||
            request.Arguments.Sum(item => (long)item.Length) > 32_768 || request.Timeout <= TimeSpan.Zero ||
            request.Timeout > TimeSpan.FromMinutes(5) || request.MaximumOutputBytes is <= 0 or > 1_048_576)
            return DomainResult.Fail<bool>(new DomainFailure(
                FailureCode.ValidationFailure, "Container arguments, time, or output bounds are invalid."));
        return DomainResult.Success(true);
    }

    private DomainResult<string> ResolveContainerExecutable(string hostExecutable)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(hostExecutable) || !Path.IsPathFullyQualified(hostExecutable))
                return InvalidExecutable();
            var path = Path.GetFullPath(hostExecutable);
            var info = new FileInfo(path);
            if (!info.Exists || info.LinkTarget is not null ||
                (info.Attributes & FileAttributes.ReparsePoint) != 0 || info.DirectoryName is null ||
                WorkspacePathGuard.ContainsLinkOrReparsePoint(info.DirectoryName) ||
                !_options.ExecutableMappings.TryGetValue(info.Name, out var mapped) ||
                !ContainerPathPattern().IsMatch(mapped))
                return InvalidExecutable();
            return DomainResult.Success(mapped);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or
            NotSupportedException or PathTooLongException or UnauthorizedAccessException)
        {
            return InvalidExecutable();
        }
    }

    private ProcessExecutionRequest RuntimeRequest(
        IReadOnlyList<string> arguments,
        string workspace,
        TimeSpan timeout,
        int outputBytes) => new(
            Path.GetFullPath(_options.RuntimeExecutable),
            arguments,
            workspace,
            workspace,
            new Dictionary<string, string>(),
            timeout,
            outputBytes,
            ProcessNetworkPolicy.InheritHost,
            ProcessSandboxKind.RestrictedHost,
            ProcessIsolationFeature.DirectExecutable | ProcessIsolationFeature.ArgumentArray |
            ProcessIsolationFeature.EnvironmentAllowlist | ProcessIsolationFeature.WorkingDirectoryContainment |
            ProcessIsolationFeature.BoundedOutput | ProcessIsolationFeature.WallClockTimeout |
            ProcessIsolationFeature.ProcessTreeTermination);

    private async Task CleanupAsync(string name, string workspace)
    {
        try
        {
            _ = await _runtime.InvokeAsync(RuntimeRequest(
                ["rm", "--force", name], workspace, TimeSpan.FromSeconds(_options.CleanupTimeoutSeconds), 4096),
                null,
                CancellationToken.None);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or OperationCanceledException)
        {
            // The one-shot container may already have been removed by --rm.
        }
    }

    internal static bool IsConfigured(DockerSandboxOptions options) =>
        !string.IsNullOrWhiteSpace(options.RuntimeExecutable) && Path.IsPathFullyQualified(options.RuntimeExecutable) &&
        ImagePattern().IsMatch(options.ImageReference) && UserPattern().IsMatch(options.ContainerUser) &&
        options.ContainerUser is not ("0" or "0:0") && options.MemoryMegabytes is >= 64 and <= 16_384 &&
        options.CpuLimit is >= 0.1m and <= 32m && options.ProcessLimit is >= 8 and <= 4096 &&
        options.TemporaryMegabytes is >= 8 and <= 1024 && options.CleanupTimeoutSeconds is >= 1 and <= 120 &&
        options.ExecutableMappings is { Count: >= 1 and <= 64 } &&
        options.ExecutableMappings.All(item => FileNamePattern().IsMatch(item.Key) && ContainerPathPattern().IsMatch(item.Value));

    private static DomainResult<ProcessExecutionResult> Unsupported(string message) =>
        DomainResult.Fail<ProcessExecutionResult>(new DomainFailure(FailureCode.UnsupportedCapability, message));

    private static DomainResult<bool> UnsupportedBoolean(string message) =>
        DomainResult.Fail<bool>(new DomainFailure(FailureCode.UnsupportedCapability, message));

    private static DomainResult<string> InvalidExecutable() =>
        DomainResult.Fail<string>(new DomainFailure(
            FailureCode.PolicyDenied, "Container executable identity is unavailable, linked, or not allowlisted."));

    [GeneratedRegex("^(?:[a-z0-9][a-z0-9._/-]{0,255}@)?sha256:[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex ImagePattern();

    [GeneratedRegex("^[1-9][0-9]{0,9}(?::[1-9][0-9]{0,9})?$", RegexOptions.CultureInvariant)]
    private static partial Regex UserPattern();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex FileNamePattern();

    [GeneratedRegex("^/(?:[A-Za-z0-9._-]+/)*[A-Za-z0-9._-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex ContainerPathPattern();
}
