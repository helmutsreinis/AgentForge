using System.Collections.Immutable;
using System.Text.Json;
using AgentForge.Abstractions.Channels;
using AgentForge.Domain.Channels;
using AgentForge.Domain.Primitives;

namespace AgentForge.Channels;

public sealed class DeterministicChannelAdapter(
    ChannelKind kind,
    string accountId,
    string webhookSecret,
    IEnumerable<ChannelAdapterSendResult>? scriptedSends = null) : IChannelAdapter
{
    private readonly Queue<ChannelAdapterSendResult> _sends = new(scriptedSends ?? []);
    public ChannelKind Kind { get; } = kind;
    public string AccountId { get; } = accountId;
    public int SendCount { get; private set; }

    public Task<DomainResult<ParsedChannelMessage>> AuthenticateAndParseAsync(
        ChannelWebhookRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var header = request.Headers.GetValueOrDefault("X-AgentForge-Webhook-Secret");
        if (!CryptographicEquals(header, webhookSecret) || request.Body.Length is < 2 or > 1_048_576)
        {
            return Task.FromResult(DomainResult.Fail<ParsedChannelMessage>(new DomainFailure(
                FailureCode.PolicyDenied, "Webhook authentication failed.")));
        }

        try
        {
            using var document = JsonDocument.Parse(request.Body, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            var attachments = root.TryGetProperty("attachments", out var array)
                ? array.EnumerateArray().Select(item => new ChannelAttachment(
                    item.GetProperty("fileName").GetString()!, item.GetProperty("mediaType").GetString()!,
                    item.GetProperty("length").GetInt64(), item.GetProperty("contentHash").GetString()!,
                    AttachmentScanStatus.Clean)).ToImmutableArray()
                : [];
            var parsed = new ParsedChannelMessage(
                root.GetProperty("messageId").GetString()!, root.GetProperty("senderId").GetString()!,
                root.GetProperty("recipientId").GetString()!, root.GetProperty("text").GetString()!, attachments,
                DateTimeOffset.FromUnixTimeSeconds(root.GetProperty("timestamp").GetInt64()),
                ChannelEvidence.Hash($"auth:{Kind}:{AccountId}:{ChannelEvidence.Hash(Convert.ToHexString(request.Body.Span))}"));
            return Task.FromResult(DomainResult.Success(parsed));
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return Task.FromResult(DomainResult.Fail<ParsedChannelMessage>(new DomainFailure(
                FailureCode.ValidationFailure, "Webhook payload is invalid.")));
        }
    }

    public Task<ChannelAdapterSendResult> SendAsync(
        ChannelSendRequest request, string requestHash, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SendCount++;
        return Task.FromResult(_sends.Count > 0 ? _sends.Dequeue() : new ChannelAdapterSendResult(
            true, false, false, $"fake-{SendCount}", ChannelEvidence.Hash($"sent:{requestHash}:{SendCount}")));
    }

    private static bool CryptographicEquals(string? left, string right)
    {
        if (left is null) return false;
        var leftBytes = System.Text.Encoding.UTF8.GetBytes(left);
        var rightBytes = System.Text.Encoding.UTF8.GetBytes(right);
        return leftBytes.Length == rightBytes.Length &&
            System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }
}
