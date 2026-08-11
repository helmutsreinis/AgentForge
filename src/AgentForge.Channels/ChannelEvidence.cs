using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentForge.Domain.Channels;

namespace AgentForge.Channels;

internal static class ChannelEvidence
{
    public static string Hash(string value) => $"sha256:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))}";

    public static string ContentHash(string text, IEnumerable<ChannelAttachment> attachments) =>
        Hash($"v1\n{text}\n{string.Join('\n', attachments.Select(item => $"{item.FileName}|{item.MediaType}|{item.Length}|{item.ContentHash}|{item.ScanStatus}"))}");

    public static string RequestHash(ChannelSendRequest request, string contentHash) => Hash(JsonSerializer.Serialize(new
    {
        v = 1,
        installation = request.InstallationId.Value,
        request.ExpectedInstallationVersion,
        agent = request.AgentId.Value,
        request.AgentVersion,
        actor = request.ActorId.Value,
        channel = request.Channel.ToString(),
        request.AccountId,
        request.RecipientId,
        contentHash,
        request.Policy,
        correlation = request.CorrelationId.Value,
    }));
}
