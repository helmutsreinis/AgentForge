namespace AgentForge.Persistence.Entities;

internal sealed class TrajectoryExportEntity
{
    public Guid Id { get; set; }

    public Guid InstallationId { get; set; }

    public string IdempotencyKey { get; set; } = string.Empty;

    public string RequestHash { get; set; } = string.Empty;

    public string ArtifactContentHash { get; set; } = string.Empty;

    public string ResultJson { get; set; } = string.Empty;

    public long CreatedAtUtcTicks { get; set; }
}
