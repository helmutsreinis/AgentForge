using System.Text.Json;
using AgentForge.Abstractions.Channels;
using AgentForge.Domain.Agents;
using AgentForge.Domain.Channels;
using AgentForge.Domain.Primitives;
using AgentForge.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace AgentForge.Persistence;

internal sealed class SqliteChannelRepository(AgentForgeDbContext dbContext) :
    IChannelRepository, IChannelIdentityResolver, IChannelIdentityBindingStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async ValueTask<ChannelIdentityBinding?> ResolveAsync(
        ChannelKind channel, string accountId, string externalSenderId, CancellationToken cancellationToken)
    {
        var row = await dbContext.ChannelIdentityBindings.AsNoTracking().SingleOrDefaultAsync(item =>
            item.Channel == channel.ToString() && item.AccountId == accountId &&
            item.ExternalSenderId == externalSenderId, cancellationToken);
        return row is null ? null : new ChannelIdentityBinding(
            new InstallationId(row.InstallationId), new AgentIdentityId(row.AgentId), new ActorId(row.ActorId),
            channel, row.AccountId, row.ExternalSenderId, row.EvidenceHash);
    }

    public async ValueTask AddAsync(ChannelIdentityBinding binding, CancellationToken cancellationToken)
    {
        await dbContext.ChannelIdentityBindings.AddAsync(new ChannelIdentityBindingEntity
        {
            InstallationId = binding.InstallationId.Value,
            AgentId = binding.AgentId.Value,
            ActorId = binding.ActorId.Value,
            Channel = binding.Channel.ToString(),
            AccountId = binding.AccountId,
            ExternalSenderId = binding.ExternalSenderId,
            EvidenceHash = binding.EvidenceHash,
        }, cancellationToken);
    }

    public async ValueTask<NormalizedInboundChannelMessage?> FindInboundAsync(
        ChannelKind channel, string accountId, string externalMessageId, CancellationToken cancellationToken)
    {
        var row = await dbContext.ChannelInboundMessages.AsNoTracking().SingleOrDefaultAsync(item =>
            item.Channel == channel.ToString() && item.AccountId == accountId &&
            item.ExternalMessageId == externalMessageId, cancellationToken);
        return row is null ? null : DeserializeInbound(row);
    }

    public async ValueTask AddInboundAsync(NormalizedInboundChannelMessage message, CancellationToken cancellationToken)
    {
        await dbContext.ChannelInboundMessages.AddAsync(new ChannelInboundMessageEntity
        {
            Id = message.Id.Value,
            InstallationId = message.InstallationId.Value,
            AgentId = message.AgentId.Value,
            Channel = message.Channel.ToString(),
            AccountId = message.AccountId,
            ExternalMessageId = message.ExternalMessageId,
            MessageHash = message.MessageHash,
            OrderKey = message.OrderKey,
            ReceivedAtUtcTicks = message.ReceivedAtUtc.UtcTicks,
            MessageJson = JsonSerializer.Serialize(message, JsonOptions),
        }, cancellationToken);
    }

    public async ValueTask<ChannelDelivery?> FindDeliveryByIdempotencyKeyAsync(
        InstallationId installationId, string idempotencyKey, CancellationToken cancellationToken)
    {
        var row = await dbContext.ChannelDeliveries.AsNoTracking().SingleOrDefaultAsync(item =>
            item.InstallationId == installationId.Value && item.IdempotencyKey == idempotencyKey, cancellationToken);
        return row is null ? null : DeserializeDelivery(row);
    }

    public async ValueTask AddDeliveryAsync(ChannelDelivery delivery, CancellationToken cancellationToken)
    {
        await dbContext.ChannelDeliveries.AddAsync(Map(delivery), cancellationToken);
    }

    public async ValueTask UpdateDeliveryAsync(ChannelDelivery delivery, long expectedVersion, CancellationToken cancellationToken)
    {
        var row = await dbContext.ChannelDeliveries.SingleAsync(item => item.Id == delivery.Id.Value, cancellationToken);
        if (row.Version != expectedVersion) throw new DbUpdateConcurrencyException("Channel delivery version is stale.");
        dbContext.Entry(row).CurrentValues.SetValues(Map(delivery));
        dbContext.Entry(row).Property(item => item.Version).OriginalValue = expectedVersion;
    }

    public async ValueTask<int> CountSentAsync(
        InstallationId installationId, AgentIdentityId agentId, ChannelKind channel,
        DateTimeOffset sinceUtc, CancellationToken cancellationToken) => await dbContext.ChannelDeliveries.AsNoTracking()
            .CountAsync(item => item.InstallationId == installationId.Value && item.AgentId == agentId.Value &&
                item.Channel == channel.ToString() && item.State == ChannelDeliveryState.Sent.ToString() &&
                item.UpdatedAtUtcTicks >= sinceUtc.UtcTicks, cancellationToken);

    private static ChannelDeliveryEntity Map(ChannelDelivery delivery) => new()
    {
        Id = delivery.Id.Value,
        InstallationId = delivery.InstallationId.Value,
        AgentId = delivery.AgentId.Value,
        Channel = delivery.Channel.ToString(),
        State = delivery.State.ToString(),
        IdempotencyKey = delivery.IdempotencyKey,
        RequestHash = delivery.RequestHash,
        UpdatedAtUtcTicks = delivery.UpdatedAtUtc.UtcTicks,
        Version = delivery.Version,
        DeliveryJson = JsonSerializer.Serialize(delivery, JsonOptions),
    };

    private static NormalizedInboundChannelMessage DeserializeInbound(ChannelInboundMessageEntity row)
    {
        var value = JsonSerializer.Deserialize<NormalizedInboundChannelMessage>(row.MessageJson, JsonOptions)
            ?? throw new InvalidOperationException("Persisted channel message is empty.");
        return value.Id.Value == row.Id && value.MessageHash == row.MessageHash && value.OrderKey == row.OrderKey
            ? value : throw new InvalidOperationException("Persisted channel message failed integrity validation.");
    }

    private static ChannelDelivery DeserializeDelivery(ChannelDeliveryEntity row)
    {
        var value = JsonSerializer.Deserialize<ChannelDelivery>(row.DeliveryJson, JsonOptions)
            ?? throw new InvalidOperationException("Persisted channel delivery is empty.");
        return value.Id.Value == row.Id && value.Version == row.Version && value.RequestHash == row.RequestHash &&
            value.State.ToString() == row.State
            ? value : throw new InvalidOperationException("Persisted channel delivery failed integrity validation.");
    }
}
