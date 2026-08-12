namespace AgentForge.Persistence.Entities;

internal sealed class SerialCaptureEntity
{
    public Guid Id { get; init; }
    public Guid InstallationId { get; init; }
    public Guid AgentId { get; init; }
    public required string PhysicalDeviceId { get; init; }
    public required string ArtifactContentHash { get; init; }
    public required string StreamHash { get; init; }
    public required string RequestHash { get; init; }
    public required string IdempotencyKey { get; init; }
    public long StartedAtUtcTicks { get; init; }
    public long Version { get; init; }
    public required string CaptureJson { get; init; }
}
