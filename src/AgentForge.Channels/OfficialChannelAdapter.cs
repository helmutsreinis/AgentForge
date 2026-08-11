using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentForge.Abstractions.Channels;
using AgentForge.Abstractions.Security;
using AgentForge.Domain.Channels;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Security;

namespace AgentForge.Channels;

public sealed record OfficialChannelOptions(
    string AccountId,
    SecretReference WebhookSecret,
    SecretReference SendCredential,
    Uri SendBaseUri,
    int MaximumResponseBytes = 262_144,
    TimeSpan? Timeout = null);

public sealed class OfficialChannelAdapter : IChannelAdapter, IDisposable
{
    private readonly OfficialChannelOptions _options;
    private readonly ISecretStore _secrets;
    private readonly HttpClient _client;

    private OfficialChannelAdapter(
        ChannelKind kind,
        OfficialChannelOptions options,
        ISecretStore secrets,
        HttpMessageHandler handler)
    {
        Kind = kind;
        AccountId = options.AccountId;
        _options = options;
        _secrets = secrets;
        _client = new HttpClient(handler, true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public ChannelKind Kind { get; }
    public string AccountId { get; }

    public static DomainResult<OfficialChannelAdapter> CreateTelegram(
        OfficialChannelOptions options, ISecretStore secrets) =>
        Create(ChannelKind.Telegram, options, secrets, CreateHandler());

    public static DomainResult<OfficialChannelAdapter> CreateWhatsApp(
        OfficialChannelOptions options, ISecretStore secrets) =>
        Create(ChannelKind.WhatsApp, options, secrets, CreateHandler());

    internal static DomainResult<OfficialChannelAdapter> CreateForTesting(
        ChannelKind kind, OfficialChannelOptions options, ISecretStore secrets, HttpMessageHandler handler) =>
        Create(kind, options, secrets, handler);

    public async Task<DomainResult<ParsedChannelMessage>> AuthenticateAndParseAsync(
        ChannelWebhookRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Channel != Kind || request.AccountId != AccountId)
            return Denied<ParsedChannelMessage>("Webhook adapter identity does not match.");
        var materialized = await _secrets.MaterializeAsync(_options.WebhookSecret, cancellationToken);
        if (!materialized.IsSuccess) return DomainResult.Fail<ParsedChannelMessage>(materialized.Failure!);
        await using var lease = materialized.Value;
        var valid = Kind == ChannelKind.Telegram
            ? FixedEquals(FindHeader(request.Headers, "X-Telegram-Bot-Api-Secret-Token"), lease.Value.Span)
            : ValidWhatsAppSignature(FindHeader(request.Headers, "X-Hub-Signature-256"), request.Body.Span, lease.Value.Span);
        if (!valid) return Denied<ParsedChannelMessage>("Webhook authentication failed.");
        try
        {
            using var document = JsonDocument.Parse(request.Body, new JsonDocumentOptions { MaxDepth = 32 });
            var parsed = Kind == ChannelKind.Telegram
                ? ParseTelegram(document.RootElement)
                : ParseWhatsApp(document.RootElement);
            if (!parsed.IsSuccess) return parsed;
            var bodyHash = ChannelEvidence.Hash(Convert.ToHexString(request.Body.Span));
            return DomainResult.Success(parsed.Value with
            {
                AuthenticationEvidenceHash = ChannelEvidence.Hash($"v1:{Kind}:{AccountId}:{bodyHash}"),
            });
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return Invalid<ParsedChannelMessage>("Official webhook payload is malformed.");
        }
    }

    public async Task<ChannelAdapterSendResult> SendAsync(
        ChannelSendRequest request,
        string requestHash,
        CancellationToken cancellationToken)
    {
        if (request.Channel != Kind || request.AccountId != AccountId || request.Attachments.Length > 0)
            return Failed("adapter-input", false, false);
        var materialized = await _secrets.MaterializeAsync(_options.SendCredential, cancellationToken);
        if (!materialized.IsSuccess) return Failed("credential-unavailable", true, false);
        await using var lease = materialized.Value;
        if (lease.Value.Length is < 1 or > 512 || lease.Value.Span.Contains('\r') || lease.Value.Span.Contains('\n'))
            return Failed("credential-invalid", false, false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.Timeout ?? TimeSpan.FromSeconds(20));
        var token = new string(lease.Value.Span);
        HttpRequestMessage? message = null;
        try
        {
            var endpoint = Kind == ChannelKind.Telegram
                ? new Uri(_options.SendBaseUri, $"bot{token}/sendMessage")
                : _options.SendBaseUri;
            message = new HttpRequestMessage(HttpMethod.Post, endpoint);
            if (Kind == ChannelKind.WhatsApp)
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var payload = Kind == ChannelKind.Telegram
                ? JsonSerializer.SerializeToUtf8Bytes(new { chat_id = request.RecipientId, text = request.Text })
                : JsonSerializer.SerializeToUtf8Bytes(new
                {
                    messaging_product = "whatsapp",
                    to = request.RecipientId,
                    type = "text",
                    text = new { body = request.Text },
                });
            message.Content = new ByteArrayContent(payload);
            message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                return Failed("throttled", true, false);
            if (!response.IsSuccessStatusCode)
                return Failed($"http-{(int)response.StatusCode}", false, (int)response.StatusCode >= 500);
            var bytes = await ReadBoundedAsync(response.Content, timeout.Token);
            if (bytes is null) return Failed("response-oversize", false, true);
            var providerId = ParseSendId(bytes);
            return providerId is null
                ? Failed("response-invalid", false, true)
                : new ChannelAdapterSendResult(true, false, false, providerId,
                    ChannelEvidence.Hash($"v1:sent:{Kind}:{AccountId}:{requestHash}:{providerId}"));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failed("timeout", false, true);
        }
        catch (HttpRequestException)
        {
            return Failed("transport", false, true);
        }
        finally
        {
            if (message is not null)
            {
                message.Headers.Authorization = null;
                message.Dispose();
            }
            token = string.Empty;
        }
    }

    public void Dispose() => _client.Dispose();

    private static DomainResult<OfficialChannelAdapter> Create(
        ChannelKind kind, OfficialChannelOptions options, ISecretStore secrets, HttpMessageHandler handler)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(handler);
        var timeout = options.Timeout ?? TimeSpan.FromSeconds(20);
        var telegram = kind == ChannelKind.Telegram && options.SendBaseUri == new Uri("https://api.telegram.org/");
        var whatsApp = kind == ChannelKind.WhatsApp && options.SendBaseUri.Scheme == Uri.UriSchemeHttps &&
            options.SendBaseUri.Host == "graph.facebook.com" && options.SendBaseUri.IsDefaultPort &&
            options.SendBaseUri.AbsolutePath.EndsWith($"/{options.AccountId}/messages", StringComparison.Ordinal) &&
            string.IsNullOrEmpty(options.SendBaseUri.Query) && string.IsNullOrEmpty(options.SendBaseUri.Fragment);
        if (!IsText(options.AccountId, 128) || (!telegram && !whatsApp) ||
            options.WebhookSecret.Store != secrets.StoreName || options.SendCredential.Store != secrets.StoreName ||
            options.MaximumResponseBytes is < 1024 or > 1_048_576 ||
            timeout < TimeSpan.FromSeconds(1) || timeout > TimeSpan.FromSeconds(30))
        {
            handler.Dispose();
            return Invalid<OfficialChannelAdapter>("Official channel endpoint, account, secrets, or bounds are invalid.");
        }
        return DomainResult.Success(new OfficialChannelAdapter(kind, options with { }, secrets, handler));
    }

    private static DomainResult<ParsedChannelMessage> ParseTelegram(JsonElement root)
    {
        var message = root.GetProperty("message");
        var sender = message.GetProperty("from").GetProperty("id").GetInt64().ToString(System.Globalization.CultureInfo.InvariantCulture);
        var recipient = message.GetProperty("chat").GetProperty("id").GetInt64().ToString(System.Globalization.CultureInfo.InvariantCulture);
        return DomainResult.Success(new ParsedChannelMessage(
            $"{root.GetProperty("update_id").GetInt64()}:{message.GetProperty("message_id").GetInt64()}",
            sender, recipient, message.GetProperty("text").GetString() ?? string.Empty, [],
            DateTimeOffset.FromUnixTimeSeconds(message.GetProperty("date").GetInt64()), string.Empty));
    }

    private static DomainResult<ParsedChannelMessage> ParseWhatsApp(JsonElement root)
    {
        var value = root.GetProperty("entry")[0].GetProperty("changes")[0].GetProperty("value");
        var message = value.GetProperty("messages")[0];
        return DomainResult.Success(new ParsedChannelMessage(
            message.GetProperty("id").GetString()!, message.GetProperty("from").GetString()!,
            value.GetProperty("metadata").GetProperty("phone_number_id").GetString()!,
            message.GetProperty("text").GetProperty("body").GetString() ?? string.Empty, [],
            DateTimeOffset.FromUnixTimeSeconds(long.Parse(message.GetProperty("timestamp").GetString()!, System.Globalization.CultureInfo.InvariantCulture)),
            string.Empty));
    }

    private async Task<byte[]?> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > _options.MaximumResponseBytes) return null;
        await using var input = await content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream(_options.MaximumResponseBytes);
        var buffer = new byte[4096];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0) return output.ToArray();
            if (output.Length + read > _options.MaximumResponseBytes) return null;
            output.Write(buffer, 0, read);
        }
    }

    private string? ParseSendId(byte[] bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
            return Kind == ChannelKind.Telegram
                ? document.RootElement.GetProperty("result").GetProperty("message_id").GetInt64().ToString(System.Globalization.CultureInfo.InvariantCulture)
                : document.RootElement.GetProperty("messages")[0].GetProperty("id").GetString();
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return null;
        }
    }

    private ChannelAdapterSendResult Failed(string reason, bool retryable, bool uncertain) => new(
        false, retryable, uncertain, null, ChannelEvidence.Hash($"v1:failed:{Kind}:{AccountId}:{reason}"));

    private static string? FindHeader(ImmutableDictionary<string, string> headers, string name) =>
        headers.FirstOrDefault(item => string.Equals(item.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

    private static bool FixedEquals(string? supplied, ReadOnlySpan<char> expected)
    {
        if (supplied is null) return false;
        var left = Encoding.UTF8.GetBytes(supplied);
        var expectedChars = expected.ToArray();
        try
        {
            var right = Encoding.UTF8.GetBytes(expectedChars);
            return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
        }
        finally
        {
            Array.Clear(expectedChars);
        }
    }

    private static bool ValidWhatsAppSignature(string? supplied, ReadOnlySpan<byte> body, ReadOnlySpan<char> secret)
    {
        if (supplied is null || !supplied.StartsWith("sha256=", StringComparison.Ordinal) || supplied.Length != 71) return false;
        Span<byte> expected = stackalloc byte[32];
        var secretChars = secret.ToArray();
        var key = Encoding.UTF8.GetBytes(secretChars);
        try
        {
            HMACSHA256.HashData(key, body, expected);
            var actual = Convert.FromHexString(supplied[7..]);
            return actual.Length == 32 && CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch (FormatException) { return false; }
        finally { Array.Clear(key); Array.Clear(secretChars); }
    }

    private static bool IsText(string value, int maximum) => !string.IsNullOrWhiteSpace(value) &&
        value.Length <= maximum && !value.Any(char.IsControl);
    private static DomainResult<T> Invalid<T>(string message) => DomainResult.Fail<T>(new DomainFailure(FailureCode.ValidationFailure, message));
    private static DomainResult<T> Denied<T>(string message) => DomainResult.Fail<T>(new DomainFailure(FailureCode.PolicyDenied, message));
    private static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    };
}
