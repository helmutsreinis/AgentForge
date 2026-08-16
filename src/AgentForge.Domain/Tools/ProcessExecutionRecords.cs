namespace AgentForge.Domain.Tools;

public enum ProcessSandboxKind
{
    RestrictedHost,
    Container,
    BuiltIn,
}

public enum ProcessNetworkPolicy
{
    Denied,
    LoopbackOnly,
    FixedEndpointOnly,
    InheritHost,
}

public enum ProcessFileSystemPolicy
{
    ReadOnlyWorkspace,
    ReadWriteWorkspace,
}

[Flags]
public enum ProcessIsolationFeature
{
    None = 0,
    DirectExecutable = 1 << 0,
    ArgumentArray = 1 << 1,
    EnvironmentAllowlist = 1 << 2,
    WorkingDirectoryContainment = 1 << 3,
    BoundedOutput = 1 << 4,
    WallClockTimeout = 1 << 5,
    ProcessTreeTermination = 1 << 6,
    KillOnControllerExit = 1 << 7,
    NetworkIsolation = 1 << 8,
    FileSystemIsolation = 1 << 9,
    CpuLimit = 1 << 10,
    MemoryLimit = 1 << 11,
    ProcessLimit = 1 << 12,
}

public enum ProcessOutputChannel
{
    StandardOutput,
    StandardError,
}

public sealed record ProcessSandboxCapabilities(
    ProcessSandboxKind Kind,
    bool IsAvailable,
    ProcessIsolationFeature SupportedFeatures,
    string Evidence);

public sealed record ProcessExecutionRequest(
    string ExecutablePath,
    IReadOnlyList<string> Arguments,
    string WorkspaceRoot,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment,
    TimeSpan Timeout,
    int MaximumOutputBytes,
    ProcessNetworkPolicy NetworkPolicy,
    ProcessSandboxKind RequiredSandbox,
    ProcessIsolationFeature RequiredFeatures = ProcessIsolationFeature.None,
    ProcessFileSystemPolicy FileSystemPolicy = ProcessFileSystemPolicy.ReadWriteWorkspace);

public sealed record ProcessOutputChunk(
    long Sequence,
    ProcessOutputChannel Channel,
    byte[] Data);

public sealed record ProcessExecutionResult(
    int ExitCode,
    byte[] StandardOutput,
    byte[] StandardError,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    TimeSpan Duration,
    ProcessSandboxCapabilities Sandbox);
