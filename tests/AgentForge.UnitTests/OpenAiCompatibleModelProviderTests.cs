using System.Net;
using System.Net.Http.Headers;
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
using AgentForge.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AgentForge.UnitTests;

public sealed class OpenAiCompatibleModelProviderTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);
    private static readonly ProviderProfileId ProfileId = new(Guid.Parse("57c9ec4a-763c-4901-96c8-a4a8f8bc0059"));
    private static readonly Uri HttpsEndpoint = new("https://models.example.test/v1/chat/completions");

    [Fact]
    public void Creation_requires_explicit_plaintext_transport_and_rejects_multimodal_claims()
    {
        var insecure = OpenAiCompatibleModelProvider.Create(
            Descriptor(),
            new OpenAiCompatibleModelProviderOptions(new Uri("http://192.0.2.10:8000/v1/chat/completions")),
            ContextPreparer(),
            new FixedClock());
        var explicitInsecure = OpenAiCompatibleModelProvider.Create(
            Descriptor(),
            new OpenAiCompatibleModelProviderOptions(
                new Uri("http://192.0.2.10:8000/v1/chat/completions"),
                AllowInsecureHttp: true),
            ContextPreparer(),
            new FixedClock());
        var multimodal = OpenAiCompatibleModelProvider.Create(
            Descriptor(ModelCapability.ImageInput),
            new OpenAiCompatibleModelProviderOptions(HttpsEndpoint),
            ContextPreparer(),
            new FixedClock());

        Assert.False(insecure.IsSuccess);
        Assert.True(explicitInsecure.IsSuccess, explicitInsecure.Failure?.Message);
        explicitInsecure.Value.Dispose();
        Assert.False(multimodal.IsSuccess);
        Assert.Equal(FailureCode.ValidationFailure, multimodal.Failure?.Code);
    }

    [Fact]
    public async Task Text_stream_translates_request_deltas_usage_and_completion()
    {
        string? requestJson = null;
        string? correlation = null;
        using var handler = Handler(async (request, cancellationToken) =>
        {
            requestJson = await request.Content!.ReadAsStringAsync(cancellationToken);
            correlation = request.Headers.GetValues("X-Correlation-Id").Single();
            return Sse(request,
                Event("{\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"hel\"},\"finish_reason\":null}]}"),
                Event("{\"choices\":[{\"index\":0,\"delta\":{\"content\":\"lo\"},\"finish_reason\":null}]}"),
                Event("{\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":4,\"completion_tokens\":2,\"total_tokens\":6}}"),
                Done());
        });
        using var provider = CreateProvider(handler, Descriptor());

        var events = await CollectAsync(provider.StreamAsync(Request(), CancellationToken.None));

        Assert.Collection(
            events,
            item => Assert.IsType<ModelStartedEvent>(item),
            item => Assert.Equal("hel", Assert.IsType<ModelTextDeltaEvent>(item).Delta),
            item => Assert.Equal("lo", Assert.IsType<ModelTextDeltaEvent>(item).Delta),
            item => Assert.Equal(2, Assert.IsType<ModelUsageEvent>(item).Usage.OutputTokens),
            item => Assert.Equal(ModelFinishReason.Stop, Assert.IsType<ModelCompletedEvent>(item).FinishReason));
        Assert.Equal(Enumerable.Range(0, 5).Select(value => (long)value), events.Select(item => item.Sequence));
        Assert.Equal("model-unit-test", correlation);
        using var payload = JsonDocument.Parse(requestJson!);
        Assert.Equal("qwen3.6", payload.RootElement.GetProperty("model").GetString());
        Assert.True(payload.RootElement.GetProperty("stream").GetBoolean());
        Assert.Equal(100, payload.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.False(payload.RootElement.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean());
        Assert.Equal("hello", payload.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
    }

    [Fact]
    public async Task Structured_stream_is_accumulated_validated_and_emitted_atomically()
    {
        using var handler = Handler((request, _) => Task.FromResult(Sse(request,
            Event("{\"choices\":[{\"index\":0,\"delta\":{\"content\":\"{\\\"answer\\\":\"},\"finish_reason\":null}]}"),
            Event("{\"choices\":[{\"index\":0,\"delta\":{\"content\":\"42}\"},\"finish_reason\":\"stop\"}]}"),
            Done())));
        using var provider = CreateProvider(handler, Descriptor(ModelCapability.StructuredOutput));
        var request = Request() with
        {
            ResponseFormat = new ModelResponseFormat(
                ModelResponseFormatKind.JsonSchema,
                "{\"type\":\"object\",\"required\":[\"answer\"]}"),
        };

        var events = await CollectAsync(provider.StreamAsync(request, CancellationToken.None));

        Assert.Collection(
            events,
            item => Assert.IsType<ModelStartedEvent>(item),
            item => Assert.Equal("{\"answer\":42}", Assert.IsType<ModelStructuredOutputEvent>(item).Json),
            item => Assert.IsType<ModelCompletedEvent>(item));
        Assert.DoesNotContain(events, item => item is ModelTextDeltaEvent);
    }

    [Fact]
    public async Task Tool_stream_requires_an_exact_listed_tool_and_complete_object_arguments()
    {
        string? requestJson = null;
        using var handler = Handler(async (request, cancellationToken) =>
        {
            requestJson = await request.Content!.ReadAsStringAsync(cancellationToken);
            return Sse(request,
                Event("{\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call-1\",\"type\":\"function\",\"function\":{\"name\":\"read_file\",\"arguments\":\"{\\\"path\\\":\"}}]},\"finish_reason\":null}]}"),
                Event("{\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"\\\"a.cs\\\"}\"}}]},\"finish_reason\":\"tool_calls\"}]}"),
                Done());
        });
        using var provider = CreateProvider(handler, Descriptor(ModelCapability.ToolCalls));
        var request = Request() with
        {
            Messages =
            [
                new ModelMessage(ModelMessageRole.User, [new ModelTextContent("Read the previous file.")]),
                new ModelMessage(ModelMessageRole.Assistant,
                [
                    new ModelToolCallContent("call-previous", "read_file", "{\"path\":\"missing.cs\"}"),
                ]),
                new ModelMessage(ModelMessageRole.Tool,
                [
                    new ModelToolResultContent(
                        "call-previous",
                        "read_file",
                        "{\"code\":\"not_found\"}",
                        true),
                ]),
                new ModelMessage(ModelMessageRole.User, [new ModelTextContent("Try a.cs.")]),
            ],
            Tools =
            [
                new ModelToolDefinition(
                    "read_file",
                    "Read one workspace file.",
                    "{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"}}}"),
            ],
            Limits = new ModelInvocationLimits(100, 1, 16, 30),
        };

        var events = await CollectAsync(provider.StreamAsync(request, CancellationToken.None));

        Assert.Equal(2, events.Count(item => item is ModelToolCallDeltaEvent));
        var completed = Assert.Single(events.OfType<ModelToolCallCompletedEvent>());
        Assert.Equal("call-1", completed.ToolCallId);
        Assert.Equal("read_file", completed.ToolName);
        Assert.Equal("{\"path\":\"a.cs\"}", completed.ArgumentsJson);
        Assert.Equal(ModelFinishReason.ToolCalls, Assert.Single(events.OfType<ModelCompletedEvent>()).FinishReason);
        using var payload = JsonDocument.Parse(requestJson!);
        using var previousResult = JsonDocument.Parse(
            payload.RootElement.GetProperty("messages")[2].GetProperty("content").GetString()!);
        Assert.True(previousResult.RootElement.GetProperty("is_error").GetBoolean());
        Assert.Equal(
            "not_found",
            previousResult.RootElement.GetProperty("result").GetProperty("code").GetString());
        Assert.Equal(
            "object",
            payload.RootElement.GetProperty("tools")[0]
                .GetProperty("function")
                .GetProperty("parameters")
                .GetProperty("type")
                .GetString());
    }

    [Fact]
    public async Task Provider_errors_are_typed_without_copying_the_remote_body()
    {
        const string remoteSecret = "sk-" + "this-must-never-be-returned";
        using var handler = Handler((request, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                RequestMessage = request,
                Content = new StringContent("{\"error\":\"" + remoteSecret + "\"}"),
            };
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(7));
            return Task.FromResult(response);
        });
        using var provider = CreateProvider(handler, Descriptor());

        var events = await CollectAsync(provider.StreamAsync(Request(), CancellationToken.None));
        Assert.IsType<ModelStartedEvent>(events[0]);
        var error = Assert.IsType<ModelErrorEvent>(events[^1]);

        Assert.Equal(ModelProviderErrorCode.RateLimited, error.Error.Code);
        Assert.True(error.Error.IsRetryable);
        Assert.Equal(TimeSpan.FromSeconds(7), error.Error.RetryAfter);
        Assert.DoesNotContain(remoteSecret, error.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Malformed_duplicate_or_truncated_streams_fail_without_false_completion()
    {
        var payloads = new[]
        {
            Event("{\"choices\":[],\"choices\":[]}") + Done(),
            Event("{\"choices\":[{\"index\":0,\"delta\":{\"content\":\"partial\"},\"finish_reason\":null}]}") ,
            Event("{\"choices\":[{\"index\":0,\"delta\":{\"reasoning_content\":\"hidden\"},\"finish_reason\":null}]}") + Done(),
        };

        foreach (var payload in payloads)
        {
            using var handler = Handler((request, _) => Task.FromResult(Sse(request, payload)));
            using var provider = CreateProvider(handler, Descriptor());
            var events = await CollectAsync(provider.StreamAsync(Request(), CancellationToken.None));

            Assert.IsType<ModelErrorEvent>(events[^1]);
            Assert.DoesNotContain(events, item => item is ModelCompletedEvent);
        }
    }

    [Fact]
    public async Task Adapter_enforces_response_redirect_and_event_bounds()
    {
        using var redirectedHandler = Handler((request, _) => Task.FromResult(Sse(
            new HttpRequestMessage(HttpMethod.Post, "https://other.example.test/v1/chat/completions"),
            Done())));
        using var redirected = CreateProvider(redirectedHandler, Descriptor());
        var redirectedError = Assert.IsType<ModelErrorEvent>((await CollectAsync(
            redirected.StreamAsync(Request(), CancellationToken.None)))[^1]);
        Assert.Equal(ModelProviderErrorCode.PolicyDenied, redirectedError.Error.Code);

        using var oversizedHandler = Handler((request, _) =>
        {
            var response = Sse(request, new string('x', 2048));
            response.Content.Headers.ContentLength = 2048;
            return Task.FromResult(response);
        });
        using var oversized = CreateProvider(
            oversizedHandler,
            Descriptor(),
            new OpenAiCompatibleModelProviderOptions(
                HttpsEndpoint,
                MaximumEventBytes: 1024,
                MaximumResponseBytes: 1024));
        var oversizedError = Assert.IsType<ModelErrorEvent>((await CollectAsync(
            oversized.StreamAsync(Request(), CancellationToken.None)))[^1]);
        Assert.Equal(ModelProviderErrorCode.InvalidResponse, oversizedError.Error.Code);
    }

    [Fact]
    public async Task Internal_timeout_is_typed_while_caller_cancellation_propagates()
    {
        using var handler = Handler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException();
        });
        using var provider = CreateProvider(handler, Descriptor());
        var timedRequest = Request() with
        {
            Limits = new ModelInvocationLimits(100, 0, 16, 1),
        };

        var timeoutEvents = await CollectAsync(provider.StreamAsync(timedRequest, CancellationToken.None));
        Assert.Equal(ModelProviderErrorCode.BudgetExceeded, Assert.IsType<ModelErrorEvent>(timeoutEvents[^1]).Error.Code);

        using var canceled = new CancellationTokenSource();
        var pending = CollectAsync(provider.StreamAsync(Request(), canceled.Token));
        canceled.CancelAfter(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public async Task Event_and_output_token_budgets_fail_typed()
    {
        using var eventHandler = Handler((request, _) => Task.FromResult(Sse(request,
            Event("{\"choices\":[{\"index\":0,\"delta\":{\"content\":\"too-many-events\"},\"finish_reason\":null}]}"),
            Event("{\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}"),
            Done())));
        using var eventProvider = CreateProvider(eventHandler, Descriptor());
        var eventRequest = Request() with
        {
            Limits = new ModelInvocationLimits(100, 0, 2, 30),
        };
        var eventResult = await CollectAsync(eventProvider.StreamAsync(eventRequest, CancellationToken.None));
        Assert.Collection(
            eventResult,
            item => Assert.IsType<ModelStartedEvent>(item),
            item => Assert.Equal(
                ModelProviderErrorCode.BudgetExceeded,
                Assert.IsType<ModelErrorEvent>(item).Error.Code));

        using var tokenHandler = Handler((request, _) => Task.FromResult(Sse(request,
            Event("{\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"length\"}],\"usage\":{\"prompt_tokens\":4,\"completion_tokens\":101}}"),
            Done())));
        using var tokenProvider = CreateProvider(tokenHandler, Descriptor());
        var tokenResult = await CollectAsync(tokenProvider.StreamAsync(Request(), CancellationToken.None));
        Assert.Equal(
            ModelProviderErrorCode.BudgetExceeded,
            Assert.IsType<ModelErrorEvent>(tokenResult[^1]).Error.Code);
        Assert.DoesNotContain(tokenResult, item => item is ModelCompletedEvent);
    }

    [Fact]
    public async Task Invalid_utf8_and_non_sse_success_responses_fail_typed()
    {
        using var utf8Handler = Handler((request, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new ByteArrayContent([.. "data: "u8, 0xff, (byte)'\n', (byte)'\n']),
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
            return Task.FromResult(response);
        });
        using var utf8Provider = CreateProvider(utf8Handler, Descriptor());
        var utf8Events = await CollectAsync(utf8Provider.StreamAsync(Request(), CancellationToken.None));
        Assert.Equal(
            ModelProviderErrorCode.InvalidResponse,
            Assert.IsType<ModelErrorEvent>(utf8Events[^1]).Error.Code);

        using var jsonHandler = Handler((request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = request,
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        }));
        using var jsonProvider = CreateProvider(jsonHandler, Descriptor());
        var jsonEvents = await CollectAsync(jsonProvider.StreamAsync(Request(), CancellationToken.None));
        Assert.Equal(
            ModelProviderErrorCode.InvalidResponse,
            Assert.IsType<ModelErrorEvent>(jsonEvents[^1]).Error.Code);
    }

    [Fact]
    public async Task Context_is_redacted_before_request_serialization_and_records_evidence()
    {
        const string shapedSecret = "Bearer abcdefghijklmnopqrstuvwxyz";
        string? requestJson = null;
        using var handler = Handler(async (request, cancellationToken) =>
        {
            requestJson = await request.Content!.ReadAsStringAsync(cancellationToken);
            return Sse(request,
                Event("{\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}"),
                Done());
        });
        using var provider = CreateProvider(handler, Descriptor());
        var original = Request() with
        {
            Messages =
            [
                new ModelMessage(ModelMessageRole.User, [new ModelTextContent(shapedSecret)]),
            ],
        };

        var events = await CollectAsync(provider.StreamAsync(original, CancellationToken.None));

        var started = Assert.IsType<ModelStartedEvent>(events[0]);
        Assert.Equal(1, started.ContextRedactionCount);
        Assert.Equal(ModelContextPreparer.PolicyName, started.ContextPreparationPolicy);
        Assert.DoesNotContain(shapedSecret, requestJson, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", requestJson, StringComparison.Ordinal);
        Assert.Equal(shapedSecret, Assert.IsType<ModelTextContent>(original.Messages[0].Content[0]).Text);
        Assert.IsType<ModelCompletedEvent>(events[^1]);
    }

    [Fact]
    public async Task Hosted_profile_materializes_exact_bearer_only_for_send_and_clears_the_lease()
    {
        const string credential = "hosted-credential-unit-value";
        var store = new TrackingSecretStore(credential);
        string? observedScheme = null;
        string? observedCredential = null;
        string? requestJson = null;
        HttpRequestMessage? capturedRequest = null;
        using var handler = Handler(async (request, cancellationToken) =>
        {
            capturedRequest = request;
            observedScheme = request.Headers.Authorization?.Scheme;
            observedCredential = request.Headers.Authorization?.Parameter;
            requestJson = await request.Content!.ReadAsStringAsync(cancellationToken);
            return Sse(request,
                Event("{\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}"),
                Done());
        });
        using var provider = OpenAiCompatibleModelProvider.CreateHostedForTesting(
            HostedProfile(store),
            Descriptor(),
            new OpenAiCompatibleModelProviderOptions(HttpsEndpoint, DisableThinking: true),
            handler,
            ContextPreparer(),
            store,
            new FixedClock()).Value;

        var events = await CollectAsync(provider.StreamAsync(Request(), CancellationToken.None));

        Assert.Equal("Bearer", observedScheme);
        Assert.Equal(credential, observedCredential);
        Assert.DoesNotContain(credential, requestJson, StringComparison.Ordinal);
        Assert.Equal(1, store.MaterializeCalls);
        Assert.NotNull(store.LastLeaseBuffer);
        Assert.All(store.LastLeaseBuffer!, character => Assert.Equal('\0', character));
        Assert.Null(capturedRequest!.Headers.Authorization);
        Assert.IsType<ModelCompletedEvent>(events[^1]);
    }

    [Fact]
    public async Task Hosted_profile_mismatch_or_missing_secret_fails_before_transport_without_leakage()
    {
        var mismatchedStore = new TrackingSecretStore("not-materialized-value");
        using var mismatchHandler = Handler((_, _) => throw new InvalidOperationException("Transport must not run."));
        var mismatch = OpenAiCompatibleModelProvider.CreateHostedForTesting(
            HostedProfile(mismatchedStore) with { Endpoint = new Uri("https://other.example.test/v1/chat/completions") },
            Descriptor(),
            new OpenAiCompatibleModelProviderOptions(HttpsEndpoint),
            mismatchHandler,
            ContextPreparer(),
            mismatchedStore,
            new FixedClock());

        Assert.False(mismatch.IsSuccess);
        Assert.Equal(0, mismatchedStore.MaterializeCalls);

        using var capabilityHandler = Handler((_, _) => throw new InvalidOperationException("Transport must not run."));
        var capabilityMismatch = OpenAiCompatibleModelProvider.CreateHostedForTesting(
            HostedProfile(mismatchedStore) with
            {
                Capabilities = HostedProfile(mismatchedStore).Capabilities with { ToolCalls = true },
            },
            Descriptor(),
            new OpenAiCompatibleModelProviderOptions(HttpsEndpoint),
            capabilityHandler,
            ContextPreparer(),
            mismatchedStore,
            new FixedClock());

        Assert.False(capabilityMismatch.IsSuccess);
        Assert.Equal(0, mismatchedStore.MaterializeCalls);

        using var locationHandler = Handler((_, _) => throw new InvalidOperationException("Transport must not run."));
        var locationMismatch = OpenAiCompatibleModelProvider.CreateHostedForTesting(
            HostedProfile(mismatchedStore),
            Descriptor(),
            new OpenAiCompatibleModelProviderOptions(
                HttpsEndpoint,
                DestinationDataLocation: ModelProviderDataLocation.PrivateNetwork),
            locationHandler,
            ContextPreparer(),
            mismatchedStore,
            new FixedClock());

        Assert.False(locationMismatch.IsSuccess);
        Assert.Equal(0, mismatchedStore.MaterializeCalls);

        var unavailableStore = new TrackingSecretStore("unavailable-secret") { FailMaterialization = true };
        var transportCalls = 0;
        using var unavailableHandler = Handler((request, _) =>
        {
            transportCalls++;
            return Task.FromResult(Sse(request, Done()));
        });
        using var unavailable = OpenAiCompatibleModelProvider.CreateHostedForTesting(
            HostedProfile(unavailableStore),
            Descriptor(),
            new OpenAiCompatibleModelProviderOptions(HttpsEndpoint),
            unavailableHandler,
            ContextPreparer(),
            unavailableStore,
            new FixedClock()).Value;

        var events = await CollectAsync(unavailable.StreamAsync(Request(), CancellationToken.None));

        Assert.Equal(0, transportCalls);
        Assert.Collection(
            events,
            item => Assert.IsType<ModelStartedEvent>(item),
            item => Assert.Equal(
                ModelProviderErrorCode.AuthenticationFailed,
                Assert.IsType<ModelErrorEvent>(item).Error.Code));
        Assert.DoesNotContain(
            "unavailable-secret",
            Assert.IsType<ModelErrorEvent>(events[^1]).Error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Hosted_credential_rejects_header_injection_and_clears_the_failed_lease()
    {
        var store = new TrackingSecretStore("token-value\r\nX-Injected: true");
        var transportCalls = 0;
        using var handler = Handler((request, _) =>
        {
            transportCalls++;
            return Task.FromResult(Sse(request, Done()));
        });
        using var provider = OpenAiCompatibleModelProvider.CreateHostedForTesting(
            HostedProfile(store),
            Descriptor(),
            new OpenAiCompatibleModelProviderOptions(HttpsEndpoint),
            handler,
            ContextPreparer(),
            store,
            new FixedClock()).Value;

        var events = await CollectAsync(provider.StreamAsync(Request(), CancellationToken.None));

        Assert.Equal(0, transportCalls);
        Assert.Equal(
            ModelProviderErrorCode.AuthenticationFailed,
            Assert.IsType<ModelErrorEvent>(events[^1]).Error.Code);
        Assert.NotNull(store.LastLeaseBuffer);
        Assert.All(store.LastLeaseBuffer!, character => Assert.Equal('\0', character));
    }

    private static OpenAiCompatibleModelProvider CreateProvider(
        HttpMessageHandler handler,
        ModelProviderDescriptor descriptor,
        OpenAiCompatibleModelProviderOptions? options = null) =>
        OpenAiCompatibleModelProvider.CreateForTesting(
            descriptor,
            options ?? new OpenAiCompatibleModelProviderOptions(
                HttpsEndpoint,
                DisableThinking: true),
            handler,
            ContextPreparer(),
            new FixedClock()).Value;

    private static IModelContextPreparer ContextPreparer()
    {
        var services = new ServiceCollection();
        services.AddAgentForgeSecurity(new ConfigurationBuilder().Build());
        services.AddAgentForgeModels();
        using var provider = services.BuildServiceProvider(validateScopes: true);
        return provider.GetRequiredService<IModelContextPreparer>();
    }

    private static ProviderProfile HostedProfile(ISecretStore store) => new(
        ProfileId,
        new InstallationId(Guid.Parse("43488023-9d94-4dc7-ab72-bd6a18f30aad")),
        "hosted-unit",
        "openai-compatible",
        HttpsEndpoint,
        "qwen3.6",
        new SecretReference(store.StoreName, "hosted-unit-reference"),
        new ProviderCapabilitySummary(
            TextGeneration: true,
            Streaming: true,
            ToolCalls: false,
            Images: false,
            EvidenceSource: "hosted-unit-evidence"),
        1,
        Now.AddHours(-1),
        Now.AddMinutes(-5),
        new ActorId("hosted-unit-operator"),
        new CorrelationId("hosted-unit-profile"));

    private static ModelProviderDescriptor Descriptor(params ModelCapability[] optionalCapabilities)
    {
        var capabilities = new List<ModelCapabilityEvidence>
        {
            Evidence(ModelCapability.TextGeneration),
            Evidence(ModelCapability.Streaming),
        };
        capabilities.AddRange(optionalCapabilities.Select(Evidence));
        return new ModelProviderDescriptor(
            ProfileId,
            "openai-compatible",
            "qwen3.6",
            capabilities,
            new ModelProviderRoutingEvidence(
                ModelProviderDataLocation.Cloud,
                ModelCapabilityEvidenceSource.PolicyApproved,
                32_768,
                4_096,
                9_500,
                null,
                null,
                100,
                Now.AddMinutes(-5),
                Now.AddMinutes(5)));
    }

    private static ModelCapabilityEvidence Evidence(ModelCapability capability) => new(
        capability,
        ModelCapabilityEvidenceSource.Declared,
        ModelCapabilityAvailability.Available,
        "OpenAI-compatible unit-test evidence.",
        Now.AddMinutes(-5));

    private static ModelRequest Request() => new(
        new ModelRequestId(Guid.Parse("054576a8-5f6a-476e-a8eb-5e23bed9a9ba")),
        "qwen3.6",
        [new ModelMessage(ModelMessageRole.User, [new ModelTextContent("hello")])],
        [],
        new ModelResponseFormat(ModelResponseFormatKind.Text),
        new ModelInvocationLimits(100, 0, 16, 30),
        0,
        1,
        42,
        new CorrelationId("model-unit-test"));

    private static CaptureHandler Handler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) =>
        new(response);

    private static HttpResponseMessage Sse(HttpRequestMessage request, params string[] parts)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = request,
            Content = new StringContent(string.Concat(parts), Encoding.UTF8, "text/event-stream"),
        };
        return response;
    }

    private static string Event(string json) => $"data: {json}\n\n";

    private static string Done() => "data: [DONE]\n\n";

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

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    private sealed class TrackingSecretStore(string value) : ISecretStore
    {
        public string StoreName => "tracking-unit-store";

        public int MaterializeCalls { get; private set; }

        public char[]? LastLeaseBuffer { get; private set; }

        public bool FailMaterialization { get; init; }

        public SecretStoreCapability GetCapability() => new(StoreName, true, null);

        public Task<DomainResult<SecretReference>> StoreAsync(
            string logicalName,
            ReadOnlyMemory<char> secret,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<DomainResult<SecretLease>> MaterializeAsync(
            SecretReference secretReference,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MaterializeCalls++;
            if (FailMaterialization)
            {
                return Task.FromResult(DomainResult.Fail<SecretLease>(new DomainFailure(
                    FailureCode.RecoverableExternalFailure,
                    "Secret fixture unavailable.",
                    true)));
            }

            LastLeaseBuffer = value.ToCharArray();
            return Task.FromResult(DomainResult.Success(new SecretLease(LastLeaseBuffer)));
        }

        public Task<DomainResult<bool>> DeleteAsync(
            SecretReference secretReference,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
