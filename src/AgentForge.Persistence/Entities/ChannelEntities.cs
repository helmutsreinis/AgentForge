namespace AgentForge.Persistence.Entities;

internal sealed class ChannelIdentityBindingEntity
{
    public Guid InstallationId { get; init; }
    public Guid AgentId { get; init; }
    public required string ActorId { get; init; }
    public required string Channel { get; init; }
    public required string AccountId { get; init; }
    public required string ExternalSenderId { get; init; }
    public required string EvidenceHash { get; init; }
}

internal sealed class ChannelInboundMessageEntity
{
    public Guid Id { get; init; }
    public Guid InstallationId { get; init; }
    public Guid AgentId { get; init; }
    public required string Channel { get; init; }
    public required string AccountId { get; init; }
    public required string ExternalMessageId { get; init; }
    public required string MessageHash { get; init; }
    public required string OrderKey { get; init; }
    public long ReceivedAtUtcTicks { get; init; }
    public required string MessageJson { get; init; }
}

internal sealed class ChannelDeliveryEntity
{
    public Guid Id { get; init; }
    public Guid InstallationId { get; init; }
    public Guid AgentId { get; init; }
    public required string Channel { get; init; }
    public required string State { get; init; }
    public required string IdempotencyKey { get; init; }
    public required string RequestHash { get; init; }
    public long UpdatedAtUtcTicks { get; init; }
    public long Version { get; init; }
    public required string DeliveryJson { get; init; }
}
