namespace AgentForge.Persistence.Entities;

internal sealed class DelegationGrantEntity
{
    public Guid Id { get; set; }

    public Guid ParentTaskId { get; set; }

    public Guid InstallationId { get; set; }

    public Guid ParentAgentId { get; set; }

    public Guid ChildAgentId { get; set; }

    public string GrantHash { get; set; } = string.Empty;

    public string GrantJson { get; set; } = string.Empty;

    public string ActorId { get; set; } = string.Empty;

    public long IssuedAtUtcTicks { get; set; }

    public long ExpiresAtUtcTicks { get; set; }

    public string CorrelationId { get; set; } = string.Empty;

    public string? CausationId { get; set; }
}
