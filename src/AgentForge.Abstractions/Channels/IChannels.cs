using AgentForge.Domain.Channels;
using AgentForge.Domain.Primitives;

namespace AgentForge.Abstractions.Channels;

public interface IChannelAdapter
{
    ChannelKind Kind { get; }
    string AccountId { get; }
    Task<DomainResult<ParsedChannelMessage>> AuthenticateAndParseAsync(ChannelWebhookRequest request, CancellationToken cancellationToken);
    Task<ChannelAdapterSendResult> SendAsync(ChannelSendRequest request, string requestHash, CancellationToken cancellationToken);
}

public interface IChannelAdapterCatalog
{
    DomainResult<IChannelAdapter> Resolve(ChannelKind kind, string accountId);
}

public interface IChannelIdentityResolver
{
    ValueTask<ChannelIdentityBinding?> ResolveAsync(
        ChannelKind channel, string accountId, string externalSenderId, CancellationToken cancellationToken);
}

public interface IChannelIdentityBindingStore
{
    ValueTask AddAsync(ChannelIdentityBinding binding, CancellationToken cancellationToken);
}

public interface IChannelAttachmentScanner
{
    ValueTask<AttachmentScanStatus> ScanAsync(ChannelAttachment attachment, CancellationToken cancellationToken);
}

public interface IChannelRepository
{
    ValueTask<NormalizedInboundChannelMessage?> FindInboundAsync(
        ChannelKind channel, string accountId, string externalMessageId, CancellationToken cancellationToken);
    ValueTask AddInboundAsync(NormalizedInboundChannelMessage message, CancellationToken cancellationToken);
    ValueTask<ChannelDelivery?> FindDeliveryByIdempotencyKeyAsync(
        InstallationId installationId, string idempotencyKey, CancellationToken cancellationToken);
    ValueTask AddDeliveryAsync(ChannelDelivery delivery, CancellationToken cancellationToken);
    ValueTask UpdateDeliveryAsync(ChannelDelivery delivery, long expectedVersion, CancellationToken cancellationToken);
    ValueTask<int> CountSentAsync(
        InstallationId installationId, AgentForge.Domain.Agents.AgentIdentityId agentId,
        ChannelKind channel, DateTimeOffset sinceUtc, CancellationToken cancellationToken);
}

public interface IChannelService
{
    Task<DomainResult<NormalizedInboundChannelMessage>> ReceiveAsync(ChannelWebhookRequest request, CancellationToken cancellationToken);
    Task<DomainResult<ChannelDelivery>> SendAsync(ChannelSendRequest request, CancellationToken cancellationToken);
}
