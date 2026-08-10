namespace AgentForge.Persistence.Entities;

internal sealed class InstallationEntity
{
    public Guid Id { get; set; }

    public string State { get; set; } = string.Empty;

    public long Version { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public string ActorId { get; set; } = string.Empty;

    public string CorrelationId { get; set; } = string.Empty;

    public string? RecoveryReason { get; set; }
}
