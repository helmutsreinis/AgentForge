namespace AgentForge.Persistence.Entities;

internal sealed class SkillActiveVersionEntity
{
    public Guid InstallationId { get; set; }
    public string SkillId { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
}
