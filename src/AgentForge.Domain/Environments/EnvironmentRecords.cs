using AgentForge.Domain.Artifacts;
using AgentForge.Domain.Primitives;

namespace AgentForge.Domain.Environments;

public enum HostOperatingSystem
{
    Unknown,
    Windows,
    Linux,
}

public enum HostArchitecture
{
    Unknown,
    X86,
    X64,
    Arm,
    Arm64,
}

public enum HostIsolationKind
{
    Unknown,
    PhysicalOrUnclassified,
    VirtualMachine,
    Container,
    WindowsSubsystemForLinux,
}

public enum HostPrivilegeLevel
{
    Unknown,
    Standard,
    Elevated,
    Root,
}

public enum EnvironmentManagerKind
{
    Package,
    Service,
}

public enum ExecutableTrust
{
    Unknown,
    SystemDirectory,
    UserDirectory,
}

public sealed record DistributionProfile(
    string Id,
    string? IdLike,
    string? VersionId,
    string? VersionCodename,
    string? PrettyName,
    bool IsKali);

public sealed record OperatingSystemProfile(
    HostOperatingSystem Family,
    string Description,
    string KernelVersion,
    HostArchitecture OperatingSystemArchitecture,
    HostArchitecture ProcessArchitecture,
    DistributionProfile? Distribution);

public sealed record WslProfile(
    bool IsWsl,
    string? DistributionName,
    int? Generation,
    string EvidenceSource);

public sealed record IsolationProfile(
    HostIsolationKind Kind,
    string EvidenceSource,
    string? ProductHint);

public sealed record FileSystemProfile(
    string CurrentRoot,
    string TemporaryRoot,
    char DirectorySeparator,
    bool? IsCaseSensitive,
    string? Format,
    string EvidenceSource);

public sealed record PrivilegeProfile(
    HostPrivilegeLevel Level,
    string EvidenceSource);

public sealed record EnvironmentManagerDescriptor(
    string Id,
    EnvironmentManagerKind Kind,
    string? Path,
    string EvidenceSource);

public sealed record ShellDescriptor(
    string Id,
    string FullPath,
    bool IsDefault,
    string EvidenceSource);

public sealed record PackageDatabaseDescriptor(
    string Id,
    int? InstalledPackageCount,
    string EvidenceSource);

public sealed record NetworkProfile(
    int InterfaceCount,
    int ActiveNonLoopbackInterfaceCount,
    bool HasLoopbackInterface,
    string EvidenceSource);

public sealed record AcceleratorDescriptor(
    string Vendor,
    string? DeviceName,
    string EvidenceSource);

public sealed record ExecutableDescriptor(
    string Name,
    string FullPath,
    long Length,
    DateTimeOffset LastWriteTimeUtc,
    bool IsSymbolicLink,
    string? LinkTarget,
    string Provenance,
    ExecutableTrust Trust);

public sealed record EnvironmentObservation(
    OperatingSystemProfile OperatingSystem,
    string FrameworkDescription,
    int ProcessorCount,
    WslProfile Wsl,
    IsolationProfile Isolation,
    FileSystemProfile FileSystem,
    PrivilegeProfile Privilege,
    IReadOnlyList<ShellDescriptor> Shells,
    IReadOnlyList<PackageDatabaseDescriptor> PackageDatabases,
    NetworkProfile Network,
    IReadOnlyList<EnvironmentManagerDescriptor> Managers,
    IReadOnlyList<AcceleratorDescriptor> Accelerators,
    IReadOnlyList<ExecutableDescriptor> Executables,
    bool ExecutableInventoryTruncated);

public sealed record EnvironmentProfile(
    int SchemaVersion,
    DateTimeOffset ObservedAt,
    ActorId ActorId,
    CorrelationId CorrelationId,
    OperatingSystemProfile OperatingSystem,
    string FrameworkDescription,
    int ProcessorCount,
    WslProfile Wsl,
    IsolationProfile Isolation,
    FileSystemProfile FileSystem,
    PrivilegeProfile Privilege,
    IReadOnlyList<ShellDescriptor> Shells,
    IReadOnlyList<PackageDatabaseDescriptor> PackageDatabases,
    NetworkProfile Network,
    IReadOnlyList<EnvironmentManagerDescriptor> Managers,
    IReadOnlyList<AcceleratorDescriptor> Accelerators,
    IReadOnlyList<ExecutableDescriptor> Executables,
    bool ExecutableInventoryTruncated,
    string Fingerprint);

public sealed record CaptureEnvironmentRequest(
    ActorId ActorId,
    CorrelationId CorrelationId);

public sealed record EnvironmentInventoryResult(
    EnvironmentProfile Profile,
    ArtifactReference Artifact,
    int RedactionCount);
