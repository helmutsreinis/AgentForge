namespace AgentForge.Persistence.Entities;

internal sealed class SkillVersionEntity
{
    public Guid InstallationId { get; set; }
    public string SkillId { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string ArtifactContentHash { get; set; } = string.Empty;
    public string PackageHash { get; set; } = string.Empty;
    public string ManifestHash { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Provenance { get; set; } = string.Empty;
    public string DescriptorJson { get; set; } = string.Empty;
    public long RecordVersion { get; set; }
    public long CreatedAtUtcTicks { get; set; }
    public long UpdatedAtUtcTicks { get; set; }
    public string ActorId { get; set; } = string.Empty;
    public string CorrelationId { get; set; } = string.Empty;
}
