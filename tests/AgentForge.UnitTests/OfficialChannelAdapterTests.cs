using System.Collections.Immutable;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using AgentForge.Abstractions.Security;
using AgentForge.Channels;
using AgentForge.Domain.Channels;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Security;

namespace AgentForge.UnitTests;

public sealed class OfficialChannelAdapterTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 12, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Telegram_authenticates_official_shape_and_sends_bounded_text()
    {
        var handler = new RecordingHandler("{\"ok\":true,\"result\":{\"message_id\":42}}");
        var store = new KeyedSecretStore();
        var created = OfficialChannelAdapter.CreateForTesting(
            ChannelKind.Telegram,
            Options(ChannelKind.Telegram), store, handler);
        Assert.True(created.IsSuccess);
        using var adapter = created.Value;
        var body = Encoding.UTF8.GetBytes("{\"update_id\":7,\"message\":{\"message_id\":9,\"from\":{\"id\":123},\"chat\":{\"id\":456},\"date\":1786536000,\"text\":\"hello\"}}");
        var parsed = await adapter.AuthenticateAndParseAsync(new ChannelWebhookRequest(
            ChannelKind.Telegram, "account-1",
            ImmutableDictionary<string, string>.Empty.Add("X-Telegram-Bot-Api-Secret-Token", "webhook-secret"),
            body, Now, new CorrelationId("telegram")), CancellationToken.None);
        var sent = await adapter.SendAsync(Send(ChannelKind.Telegram), Hash('a'), CancellationToken.None);

        Assert.True(parsed.IsSuccess);
        Assert.Equal("7:9", parsed.Value.ExternalMessageId);
        Assert.True(sent.Succeeded);
        Assert.Equal("42", sent.ProviderMessageId);
        Assert.Contains("botsend-token/sendMessage", handler.RequestUri!.AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhatsApp_requires_exact_hmac_and_parses_official_shape()
    {
        var handler = new RecordingHandler("{\"messages\":[{\"id\":\"wamid.sent\"}]}");
        var store = new KeyedSecretStore();
        var created = OfficialChannelAdapter.CreateForTesting(
            ChannelKind.WhatsApp, Options(ChannelKind.WhatsApp), store, handler);
        Assert.True(created.IsSuccess);
        using var adapter = created.Value;
        var body = Encoding.UTF8.GetBytes("{\"entry\":[{\"changes\":[{\"value\":{\"metadata\":{\"phone_number_id\":\"account-1\"},\"messages\":[{\"id\":\"wamid.in\",\"from\":\"15550001\",\"timestamp\":\"1786536000\",\"text\":{\"body\":\"hello\"}}]}}]}]}");
        var signature = $"sha256={Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes("webhook-secret"), body))}";
        var parsed = await adapter.AuthenticateAndParseAsync(new ChannelWebhookRequest(
            ChannelKind.WhatsApp, "account-1",
            ImmutableDictionary<string, string>.Empty.Add("X-Hub-Signature-256", signature),
            body, Now, new CorrelationId("whatsapp")), CancellationToken.None);
        var denied = await adapter.AuthenticateAndParseAsync(new ChannelWebhookRequest(
            ChannelKind.WhatsApp, "account-1",
            ImmutableDictionary<string, string>.Empty.Add("X-Hub-Signature-256", $"sha256={new string('0', 64)}"),
            body, Now, new CorrelationId("whatsapp")), CancellationToken.None);

        Assert.True(parsed.IsSuccess);
        Assert.Equal("wamid.in", parsed.Value.ExternalMessageId);
        Assert.Equal(FailureCode.PolicyDenied, denied.Failure?.Code);
    }

    [Fact]
    public void Official_adapter_rejects_noncanonical_send_origin()
    {
        var handler = new RecordingHandler("{}");
        var result = OfficialChannelAdapter.CreateForTesting(
            ChannelKind.Telegram,
            Options(ChannelKind.Telegram) with { SendBaseUri = new Uri("https://evil.example/") },
            new KeyedSecretStore(), handler);
        Assert.Equal(FailureCode.ValidationFailure, result.Failure?.Code);
        Assert.True(handler.Disposed);
    }

    private static OfficialChannelOptions Options(ChannelKind kind) => new(
        "account-1", new SecretReference("fake", "webhook"), new SecretReference("fake", "send"),
        kind == ChannelKind.Telegram
            ? new Uri("https://api.telegram.org/")
            : new Uri("https://graph.facebook.com/v23.0/account-1/messages"));

    private static ChannelSendRequest Send(ChannelKind kind) => new(
        new ChannelDeliveryId(Guid.NewGuid()), new InstallationId(Guid.NewGuid()), 1,
        new AgentForge.Domain.Agents.AgentIdentityId(Guid.NewGuid()), 1, new ActorId("operator"),
        kind, "account-1", "recipient", "hello", [],
        new ChannelDeliveryPolicy("UTC", new TimeOnly(1), new TimeOnly(2), 10, 3),
        "send", new CorrelationId("send"), null);

    private static string Hash(char value) => $"sha256:{new string(value, 64)}";

    private sealed class KeyedSecretStore : ISecretStore
    {
        public string StoreName => "fake";
        public SecretStoreCapability GetCapability() => new(StoreName, true, null);
        public Task<DomainResult<SecretReference>> StoreAsync(string logicalName, ReadOnlyMemory<char> secret, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<DomainResult<SecretLease>> MaterializeAsync(SecretReference secretReference, CancellationToken cancellationToken) =>
            Task.FromResult(DomainResult.Success(new SecretLease(
                (secretReference.Key == "webhook" ? "webhook-secret" : "send-token").ToCharArray())));
        public Task<DomainResult<bool>> DeleteAsync(SecretReference secretReference, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class RecordingHandler(string response) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }
        public bool Disposed { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json"),
            });
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
}
