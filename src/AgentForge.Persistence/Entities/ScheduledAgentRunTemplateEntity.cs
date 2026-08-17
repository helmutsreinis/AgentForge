namespace AgentForge.Persistence.Entities;

internal sealed class ScheduledAgentRunTemplateEntity
{
    public Guid ScheduleId { get; set; }

    public Guid InstallationId { get; set; }

    public Guid AgentId { get; set; }

    public Guid ProviderId { get; set; }

    public string SystemInstructionArtifactHash { get; set; } = string.Empty;

    public string PromptArtifactHash { get; set; } = string.Empty;

    public string TemplateHash { get; set; } = string.Empty;

    public string TemplateJson { get; set; } = string.Empty;

    public long CreatedAtUtcTicks { get; set; }
}
