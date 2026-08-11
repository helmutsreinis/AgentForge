using System.Collections.Immutable;
using AgentForge.Domain.Agents;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Security;

namespace AgentForge.Domain.Channels;

public enum ChannelKind { Telegram, WhatsApp }
public enum ChannelDeliveryState { Authorized, RetryPending, Sent, DeadLetter }
public enum AttachmentScanStatus { Clean, Rejected }

public readonly record struct ChannelMessageId(Guid Value);
public readonly record struct ChannelDeliveryId(Guid Value);

public sealed record ChannelAttachment(
    string FileName,
    string MediaType,
    long Length,
    string ContentHash,
    AttachmentScanStatus ScanStatus);

public sealed record ChannelWebhookRequest(
    ChannelKind Channel,
    string AccountId,
    ImmutableDictionary<string, string> Headers,
    ReadOnlyMemory<byte> Body,
    DateTimeOffset ReceivedAtUtc,
    CorrelationId CorrelationId);

public sealed record NormalizedInboundChannelMessage(
    ChannelMessageId Id,
    InstallationId InstallationId,
    AgentIdentityId AgentId,
    ActorId BoundActorId,
    ChannelKind Channel,
    string AccountId,
    string ExternalMessageId,
    string ExternalSenderId,
    string RecipientId,
    string Text,
    ImmutableArray<ChannelAttachment> Attachments,
    DateTimeOffset OccurredAtUtc,
    DateTimeOffset ReceivedAtUtc,
    string AuthenticationEvidenceHash,
    string MessageHash,
    string OrderKey,
    CorrelationId CorrelationId);

public sealed record ParsedChannelMessage(
    string ExternalMessageId,
    string ExternalSenderId,
    string RecipientId,
    string Text,
    ImmutableArray<ChannelAttachment> Attachments,
    DateTimeOffset OccurredAtUtc,
    string AuthenticationEvidenceHash);

public sealed record ChannelIdentityBinding(
    InstallationId InstallationId,
    AgentIdentityId AgentId,
    ActorId ActorId,
    ChannelKind Channel,
    string AccountId,
    string ExternalSenderId,
    string EvidenceHash);

public sealed record ChannelDeliveryPolicy(
    string TimeZoneId,
    TimeOnly QuietStart,
    TimeOnly QuietEnd,
    int MaximumPerHour,
    int MaximumAttempts);

public sealed record ChannelSendRequest(
    ChannelDeliveryId Id,
    InstallationId InstallationId,
    long ExpectedInstallationVersion,
    AgentIdentityId AgentId,
    long AgentVersion,
    ActorId ActorId,
    ChannelKind Channel,
    string AccountId,
    string RecipientId,
    string Text,
    ImmutableArray<ChannelAttachment> Attachments,
    ChannelDeliveryPolicy Policy,
    string IdempotencyKey,
    CorrelationId CorrelationId,
    CorrelationId? CausationId);

public sealed record ChannelAdapterSendResult(
    bool Succeeded,
    bool Retryable,
    bool DeliveryUncertain,
    string? ProviderMessageId,
    string EvidenceHash);

public sealed record ChannelDelivery(
    ChannelDeliveryId Id,
    InstallationId InstallationId,
    AgentIdentityId AgentId,
    ChannelKind Channel,
    string AccountId,
    string RecipientId,
    string RequestHash,
    string ContentHash,
    ChannelDeliveryState State,
    int AttemptCount,
    string? ProviderMessageId,
    CapabilityApprovalId? ApprovalId,
    string? LastAttemptEvidenceHash,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    string IdempotencyKey,
    ActorId ActorId,
    CorrelationId CorrelationId,
    CorrelationId? CausationId,
    long Version);
