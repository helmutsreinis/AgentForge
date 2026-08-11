using System.Net;
using System.Text;
using System.Text.Json;
using AgentForge.Abstractions.Models;
using AgentForge.Abstractions.Security;
using AgentForge.Abstractions.Time;
using AgentForge.Domain.Models;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Providers;
using AgentForge.Domain.Security;
using AgentForge.Models;

namespace AgentForge.UnitTests;

public sealed class AnthropicModelProviderTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 11, 22, 0, 0, TimeSpan.Zero);
    private static readonly Uri Endpoint = new("https://api.anthropic.example/v1/messages");
    private static readonly ProviderProfileId ProfileId = new(Guid.Parse("de32bf22-a2fc-45d8-9700-7d46960c66db"));

    [Fact]
    public async Task Text_request_and_stream_translate_to_harness_records()
    {
        HttpRequestMessage? captured = null;
        string? body = null;
        using var provider = CreateProvider(new CaptureHandler(async (request, cancellationToken) =>
        {
            captured = request;
            body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return Sse(
                request,
                MessageStart(12),
                Event("{\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}"),
                Event("{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"hello\"}}"),
                Event("{\"type\":\"content_block_stop\",\"index\":0}"),
                MessageDelta("end_turn", 4),
                Event("{\"type\":\"message_stop\"}"));
        }));

        var events = await CollectAsync(provider.StreamAsync(Request(), CancellationToken.None));

        Assert.Collection(
            events,
            item => Assert.IsType<ModelStartedEvent>(item),
            item => Assert.Equal("hello", Assert.IsType<ModelTextDeltaEvent>(item).Delta),
            item => Assert.Equal(new ModelUsage(12, 4, 0, null, null), Assert.IsType<ModelUsageEvent>(item).Usage),
            item => Assert.Equal(ModelFinishReason.Stop, Assert.IsType<ModelCompletedEvent>(item).FinishReason));
        Assert.NotNull(captured);
        Assert.Equal("2023-06-01", Assert.Single(captured.Headers.GetValues("anthropic-version")));
        Assert.False(captured.Headers.Contains("x-api-key"));
        using var json = JsonDocument.Parse(body!);
        Assert.Equal("claude-fixture", json.RootElement.GetProperty("model").GetString());
        Assert.True(json.RootElement.GetProperty("stream").GetBoolean());
        Assert.Equal(100, json.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.Equal("system instruction", json.RootElement.GetProperty("system")[0].GetProperty("text").GetString());
        Assert.Equal("hello", json.RootElement.GetProperty("messages")[0].GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task Tool_use_stream_requires_listed_tool_and_normalizes_completed_input()
    {
        using var provider = CreateProvider(
            new CaptureHandler((request, _) => Task.FromResult(Sse(
                request,
                MessageStart(20),
                Event("{\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"tool_use\",\"id\":\"toolu_1\",\"name\":\"weather\",\"input\":{}}}"),
                Event("{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"{\\\"city\\\":\"}}"),
                Event("{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"\\\"Kyiv\\\"}\"}}"),
                Event("{\"type\":\"content_block_stop\",\"index\":0}"),
                MessageDelta("tool_use", 8),
                Event("{\"type\":\"message_stop\"}")))),
            Descriptor(ModelCapability.ToolCalls));
        var request = Request() with
        {
            Tools = [new ModelToolDefinition("weather", "Get weather", "{\"type\":\"object\"}")],
            Limits = new ModelInvocationLimits(100, 1, 32, 30),
        };

        var events = await CollectAsync(provider.StreamAsync(request, CancellationToken.None));

        Assert.Equal(3, events.OfType<ModelToolCallDeltaEvent>().Count());
        var completed = Assert.Single(events.OfType<ModelToolCallCompletedEvent>());
        Assert.Equal("toolu_1", completed.ToolCallId);
        Assert.Equal("weather", completed.ToolName);
        Assert.Equal("{\"city\":\"Kyiv\"}", completed.ArgumentsJson);
        Assert.Equal(1, Assert.Single(events.OfType<ModelUsageEvent>()).Usage.ToolCalls);
        Assert.Equal(ModelFinishReason.ToolCalls, Assert.Single(events.OfType<ModelCompletedEvent>()).FinishReason);
        Assert.DoesNotContain(events, item => item is ModelErrorEvent);
    }

    [Fact]
    public async Task Malformed_or_truncated_stream_cannot_become_completion()
    {
        using var wrongModel = CreateProvider(new CaptureHandler((request, _) => Task.FromResult(Sse(
            request,
            Event("{\"type\":\"message_start\",\"message\":{\"model\":\"substituted\",\"usage\":{\"input_tokens\":1}}}")))));
        var wrongEvents = await CollectAsync(wrongModel.StreamAsync(Request(), CancellationToken.None));
        Assert.Equal(ModelProviderErrorCode.InvalidResponse, Assert.IsType<ModelErrorEvent>(wrongEvents[^1]).Error.Code);
        Assert.DoesNotContain(wrongEvents, item => item is ModelCompletedEvent);

        using var truncated = CreateProvider(new CaptureHandler((request, _) => Task.FromResult(Sse(
            request,
            MessageStart(1),
            Event("{\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}")))));
        var truncatedEvents = await CollectAsync(truncated.StreamAsync(Request(), CancellationToken.None));
        Assert.Equal(ModelProviderErrorCode.InvalidResponse, Assert.IsType<ModelErrorEvent>(truncatedEvents[^1]).Error.Code);
        Assert.DoesNotContain(truncatedEvents, item => item is ModelCompletedEvent);
    }

    [Fact]
    public async Task Hosted_key_exists_only_during_send_and_lease_is_cleared()
    {
        var store = new TrackingSecretStore("anthropic-test-key");
        string? observedKey = null;
        HttpRequestMessage? captured = null;
        using var handler = new CaptureHandler((request, _) =>
        {
            captured = request;
            observedKey = Assert.Single(request.Headers.GetValues("x-api-key"));
            return Task.FromResult(Sse(
                request,
                MessageStart(1),
                Event("{\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}"),
                Event("{\"type\":\"content_block_stop\",\"index\":0}"),
                MessageDelta("end_turn", 1),
                Event("{\"type\":\"message_stop\"}")));
        });
        using var provider = AnthropicModelProvider.CreateHostedForTesting(
            HostedProfile(store),
            Descriptor(),
            new AnthropicModelProviderOptions(Endpoint),
            handler,
            new PassthroughPreparer(),
            store,
            new FixedClock()).Value;

        var events = await CollectAsync(provider.StreamAsync(Request(), CancellationToken.None));

        Assert.Equal("anthropic-test-key", observedKey);
        Assert.NotNull(captured);
        Assert.False(captured.Headers.Contains("x-api-key"));
        Assert.Equal(1, store.MaterializeCalls);
        Assert.NotNull(store.LastBuffer);
        Assert.All(store.LastBuffer!, character => Assert.Equal('\0', character));
        Assert.IsType<ModelCompletedEvent>(events[^1]);
    }

    [Fact]
    public async Task Error_event_is_typed_bounded_and_never_echoes_remote_message()
    {
        using var provider = CreateProvider(new CaptureHandler((request, _) => Task.FromResult(Sse(
            request,
            Event("{\"type\":\"error\",\"error\":{\"type\":\"overloaded_error\",\"message\":\"remote-sensitive-detail\"}}")))));

        var events = await CollectAsync(provider.StreamAsync(Request(), CancellationToken.None));

        var error = Assert.IsType<ModelErrorEvent>(events[^1]).Error;
        Assert.Equal(ModelProviderErrorCode.ProviderUnavailable, error.Code);
        Assert.True(error.IsRetryable);
        Assert.DoesNotContain("remote-sensitive-detail", error.Message, StringComparison.Ordinal);
    }

    private static AnthropicModelProvider CreateProvider(
        HttpMessageHandler handler,
        ModelProviderDescriptor? descriptor = null) =>
        AnthropicModelProvider.CreateForTesting(
            descriptor ?? Descriptor(),
            new AnthropicModelProviderOptions(Endpoint),
            handler,
            new PassthroughPreparer(),
            new FixedClock()).Value;

    private static ModelProviderDescriptor Descriptor(params ModelCapability[] optional)
    {
        var capabilities = new List<ModelCapabilityEvidence>
        {
            Evidence(ModelCapability.TextGeneration),
            Evidence(ModelCapability.Streaming),
        };
        capabilities.AddRange(optional.Select(Evidence));
        return new ModelProviderDescriptor(
            ProfileId,
            "anthropic",
            "claude-fixture",
            capabilities,
            new ModelProviderRoutingEvidence(
                ModelProviderDataLocation.Cloud,
                ModelCapabilityEvidenceSource.PolicyApproved,
                200_000,
                8_192,
                9_500,
                null,
                null,
                500,
                Now.AddMinutes(-5),
                Now.AddMinutes(5)));
    }

    private static ModelCapabilityEvidence Evidence(ModelCapability capability) => new(
        capability,
        ModelCapabilityEvidenceSource.Declared,
        ModelCapabilityAvailability.Available,
        "Anthropic unit fixture.",
        Now.AddMinutes(-5));

    private static ModelRequest Request() => new(
        new ModelRequestId(Guid.Parse("08f969eb-c5b1-4788-bf25-f91f0bb4156d")),
        "claude-fixture",
        [
            new ModelMessage(ModelMessageRole.System, [new ModelTextContent("system instruction")]),
            new ModelMessage(ModelMessageRole.User, [new ModelTextContent("hello")]),
        ],
        [],
        new ModelResponseFormat(ModelResponseFormatKind.Text),
        new ModelInvocationLimits(100, 0, 32, 30),
        0,
        1,
        42,
        new CorrelationId("anthropic-unit"));

    private static ProviderProfile HostedProfile(ISecretStore store) => new(
        ProfileId,
        new InstallationId(Guid.Parse("2db282ec-d8cd-4a28-9722-9476918858e9")),
        "anthropic-unit",
        "anthropic",
        Endpoint,
        "claude-fixture",
        new SecretReference(store.StoreName, "anthropic-key"),
        new ProviderCapabilitySummary(true, true, false, false, "anthropic-unit"),
        1,
        Now.AddHours(-1),
        Now.AddMinutes(-5),
        new ActorId("operator"),
        new CorrelationId("anthropic-profile"));

    private static string MessageStart(long inputTokens) => Event(
        $"{{\"type\":\"message_start\",\"message\":{{\"model\":\"claude-fixture\",\"usage\":{{\"input_tokens\":{inputTokens}}}}}}}");

    private static string MessageDelta(string reason, long outputTokens) => Event(
        $"{{\"type\":\"message_delta\",\"delta\":{{\"stop_reason\":\"{reason}\"}},\"usage\":{{\"output_tokens\":{outputTokens}}}}}");

    private static string Event(string json) => $"data: {json}\n\n";

    private static HttpResponseMessage Sse(HttpRequestMessage request, params string[] events) => new(HttpStatusCode.OK)
    {
        RequestMessage = request,
        Content = new StringContent(string.Concat(events), Encoding.UTF8, "text/event-stream"),
    };

    private static async Task<List<ModelStreamEvent>> CollectAsync(IAsyncEnumerable<ModelStreamEvent> stream)
    {
        var events = new List<ModelStreamEvent>();
        await foreach (var item in stream)
        {
            events.Add(item);
        }

        return events;
    }

    private sealed class CaptureHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => response(request, cancellationToken);
    }

    private sealed class PassthroughPreparer : IModelContextPreparer
    {
        public DomainResult<PreparedModelContext> Prepare(ModelRequest request) =>
            DomainResult.Success(new PreparedModelContext(
                request,
                0,
                ModelContextPreparer.PolicyName,
                "sha256:" + new string('a', 64)));
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    private sealed class TrackingSecretStore(string value) : ISecretStore
    {
        public string StoreName => "anthropic-test-store";

        public int MaterializeCalls { get; private set; }

        public char[]? LastBuffer { get; private set; }

        public SecretStoreCapability GetCapability() => new(StoreName, true, null);

        public Task<DomainResult<SecretReference>> StoreAsync(
            string logicalName,
            ReadOnlyMemory<char> secret,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<DomainResult<SecretLease>> MaterializeAsync(
            SecretReference secretReference,
            CancellationToken cancellationToken)
        {
            MaterializeCalls++;
            LastBuffer = value.ToCharArray();
            return Task.FromResult(DomainResult.Success(new SecretLease(LastBuffer)));
        }

        public Task<DomainResult<bool>> DeleteAsync(
            SecretReference secretReference,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
