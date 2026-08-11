using AgentForge.Abstractions.Models;
using AgentForge.Abstractions.Time;
using AgentForge.Domain.Models;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Providers;
using AgentForge.Models;

namespace AgentForge.UnitTests;

public sealed class DeterministicModelProviderTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);
    private static readonly ProviderProfileId ProfileId =
        new(Guid.Parse("fa8bf238-b60e-4038-a62c-0d7241affd2f"));

    [Fact]
    public async Task Provider_streams_typed_events_in_order_and_snapshots_mutable_inputs()
    {
        var argumentDeltas = new List<string> { "{\"path\":", "\"src/file.cs\"}" };
        var scriptSteps = new List<DeterministicModelStep>
        {
            new DeterministicTextStep("Inspecting "),
            new DeterministicToolCallStep("call-001", "read_file", argumentDeltas),
            new DeterministicStructuredOutputStep("{ \"ok\": true }"),
            new DeterministicUsageStep(new ModelUsage(24, 7, 1, 0.012m, "USD")),
        };
        var provider = DeterministicModelProvider.Create(
            CreateDescriptor(
                ModelCapability.StructuredOutput,
                ModelCapability.ToolCalls,
                ModelCapability.ImageInput),
            new DeterministicModelScript(scriptSteps),
            new FixedClock()).Value;
        scriptSteps.Clear();
        argumentDeltas.Clear();

        var content = new List<ModelContent>
        {
            new ModelTextContent("Inspect this image."),
            new ModelAttachmentContent(CreateAttachment()),
        };
        var messages = new List<ModelMessage> { new(ModelMessageRole.User, content) };
        var tools = new List<ModelToolDefinition>
        {
            new("read_file", "Read one repository file.", "{ \"type\": \"object\" }"),
        };
        var request = CreateRequest(messages, tools, structured: true);
        var events = new List<ModelStreamEvent>();
        await using (var enumerator = provider.StreamAsync(request, CancellationToken.None).GetAsyncEnumerator())
        {
            Assert.True(await enumerator.MoveNextAsync());
            events.Add(enumerator.Current);
            messages.Clear();
            content.Clear();
            tools.Clear();
            while (await enumerator.MoveNextAsync())
            {
                events.Add(enumerator.Current);
            }
        }

        Assert.Equal(8, events.Count);
        Assert.Equal(Enumerable.Range(0, 8).Select(value => (long)value), events.Select(item => item.Sequence));
        var started = Assert.IsType<ModelStartedEvent>(events[0]);
        Assert.Equal(ProfileId, started.ProviderProfileId);
        Assert.StartsWith("sha256:", started.InputHash, StringComparison.Ordinal);
        Assert.StartsWith("sha256:", started.CapabilityEvidenceHash, StringComparison.Ordinal);
        Assert.Equal("Inspecting ", Assert.IsType<ModelTextDeltaEvent>(events[1]).Delta);
        var firstToolDelta = Assert.IsType<ModelToolCallDeltaEvent>(events[2]);
        Assert.Equal("read_file", firstToolDelta.ToolName);
        Assert.Null(Assert.IsType<ModelToolCallDeltaEvent>(events[3]).ToolName);
        var completedTool = Assert.IsType<ModelToolCallCompletedEvent>(events[4]);
        Assert.Equal("{\"path\":\"src/file.cs\"}", completedTool.ArgumentsJson);
        Assert.Equal("{\"ok\":true}", Assert.IsType<ModelStructuredOutputEvent>(events[5]).Json);
        Assert.Equal(7, Assert.IsType<ModelUsageEvent>(events[6]).Usage.OutputTokens);
        Assert.Equal(ModelFinishReason.Stop, Assert.IsType<ModelCompletedEvent>(events[7]).FinishReason);

        var sameInput = await FirstStartedAsync(provider, CreateRequest(
            [new ModelMessage(ModelMessageRole.User,
            [
                new ModelTextContent("Inspect this image."),
                new ModelAttachmentContent(CreateAttachment()),
            ])],
            [new ModelToolDefinition("read_file", "Read one repository file.", "{\"type\":\"object\"}")],
            structured: true));
        var changedInput = await FirstStartedAsync(provider, CreateRequest(
            [new ModelMessage(ModelMessageRole.User,
            [
                new ModelTextContent("Inspect this image."),
                new ModelAttachmentContent(CreateAttachment("b")),
            ])],
            [new ModelToolDefinition("read_file", "Read one repository file.", "{\"type\":\"object\"}")],
            structured: true));
        Assert.Equal(started.InputHash, sameInput.InputHash);
        Assert.NotEqual(started.InputHash, changedInput.InputHash);
    }

    [Fact]
    public async Task Provider_returns_typed_request_capability_and_budget_errors()
    {
        var provider = DeterministicModelProvider.Create(
            CreateDescriptor(),
            new DeterministicModelScript(
            [
                new DeterministicTextStep("bounded"),
                new DeterministicUsageStep(new ModelUsage(10, 10, 0, null, null)),
            ]),
            new FixedClock()).Value;

        var invalidTool = CreateRequest(
            [new ModelMessage(ModelMessageRole.User, [new ModelTextContent("hello")])],
            [new ModelToolDefinition("read_file", "Read.", "not-json")]);
        var invalidEvents = await CollectAsync(provider.StreamAsync(invalidTool, CancellationToken.None));
        Assert.Single(invalidEvents);
        Assert.Equal(
            ModelProviderErrorCode.InvalidRequest,
            Assert.IsType<ModelErrorEvent>(invalidEvents[0]).Error.Code);

        var unsupportedImage = CreateRequest(
            [new ModelMessage(ModelMessageRole.User, [new ModelAttachmentContent(CreateAttachment())])],
            []);
        var unsupportedEvents = await CollectAsync(provider.StreamAsync(unsupportedImage, CancellationToken.None));
        Assert.Single(unsupportedEvents);
        Assert.Equal(
            ModelProviderErrorCode.UnsupportedCapability,
            Assert.IsType<ModelErrorEvent>(unsupportedEvents[0]).Error.Code);

        var overBudget = CreateRequest(
            [new ModelMessage(ModelMessageRole.User, [new ModelTextContent("hello")])],
            []) with
        {
            Limits = new ModelInvocationLimits(5, 0, 10, 30),
        };
        var budgetEvents = await CollectAsync(provider.StreamAsync(overBudget, CancellationToken.None));
        Assert.Single(budgetEvents);
        Assert.Equal(
            ModelProviderErrorCode.BudgetExceeded,
            Assert.IsType<ModelErrorEvent>(budgetEvents[0]).Error.Code);
    }

    [Fact]
    public async Task Provider_rejects_cross_platform_attachment_paths_and_ambiguous_json()
    {
        var provider = DeterministicModelProvider.Create(
            CreateDescriptor(ModelCapability.ImageInput, ModelCapability.ToolCalls),
            new DeterministicModelScript([]),
            new FixedClock()).Value;
        foreach (var fileName in new[] { "folder/fixture.png", "folder\\fixture.png" })
        {
            var request = CreateRequest(
                [new ModelMessage(ModelMessageRole.User,
                [
                    new ModelAttachmentContent(CreateAttachment() with { FileName = fileName }),
                ])],
                []);
            var events = await CollectAsync(provider.StreamAsync(request, CancellationToken.None));
            Assert.Single(events);
            Assert.Equal(
                ModelProviderErrorCode.InvalidRequest,
                Assert.IsType<ModelErrorEvent>(events[0]).Error.Code);
        }

        var duplicateJson = CreateRequest(
            [new ModelMessage(ModelMessageRole.User, [new ModelTextContent("hello")])],
            [new ModelToolDefinition(
                "read_file",
                "Read one file.",
                "{\"type\":\"object\",\"type\":\"array\"}")]);
        var duplicateEvents = await CollectAsync(provider.StreamAsync(duplicateJson, CancellationToken.None));
        Assert.Single(duplicateEvents);
        Assert.Equal(
            ModelProviderErrorCode.InvalidRequest,
            Assert.IsType<ModelErrorEvent>(duplicateEvents[0]).Error.Code);
    }

    [Fact]
    public async Task Provider_preserves_assistant_tool_calls_and_tool_results_in_followup_input()
    {
        var provider = DeterministicModelProvider.Create(
            CreateDescriptor(ModelCapability.ToolCalls),
            new DeterministicModelScript([]),
            new FixedClock()).Value;
        var request = CreateRequest(
            [
                new ModelMessage(ModelMessageRole.User, [new ModelTextContent("Read the file.")]),
                new ModelMessage(ModelMessageRole.Assistant,
                [
                    new ModelToolCallContent("call-previous", "read_file", "{ \"path\": \"a.cs\" }"),
                ]),
                new ModelMessage(ModelMessageRole.Tool,
                [
                    new ModelToolResultContent(
                        "call-previous",
                        "read_file",
                        "{ \"content\": \"fixture\" }",
                        false),
                ]),
            ],
            [new ModelToolDefinition("read_file", "Read one file.", "{\"type\":\"object\"}")]);
        var changed = request with
        {
            Messages =
            [
                request.Messages[0],
                new ModelMessage(ModelMessageRole.Assistant,
                [
                    new ModelToolCallContent("call-previous", "read_file", "{\"path\":\"b.cs\"}"),
                ]),
                request.Messages[2],
            ],
        };

        var first = await FirstStartedAsync(provider, request);
        var second = await FirstStartedAsync(provider, changed);

        Assert.NotEqual(first.InputHash, second.InputHash);
        Assert.Equal(first.CapabilityEvidenceHash, second.CapabilityEvidenceHash);
    }

    [Fact]
    public async Task Provider_honors_stream_cancellation_during_a_scripted_pause()
    {
        var provider = DeterministicModelProvider.Create(
            CreateDescriptor(),
            new DeterministicModelScript(
            [
                new DeterministicDelayStep(TimeSpan.FromMinutes(5)),
                new DeterministicTextStep("must-not-arrive"),
            ]),
            new FixedClock()).Value;
        using var canceled = new CancellationTokenSource();
        await using var enumerator = provider.StreamAsync(
            CreateRequest(
                [new ModelMessage(ModelMessageRole.User, [new ModelTextContent("wait")])],
                []),
            canceled.Token).GetAsyncEnumerator();

        Assert.True(await enumerator.MoveNextAsync());
        Assert.IsType<ModelStartedEvent>(enumerator.Current);
        var pending = enumerator.MoveNextAsync().AsTask();
        canceled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public async Task Provider_fails_closed_when_required_capability_evidence_is_expired()
    {
        var descriptor = CreateDescriptor() with
        {
            Capabilities =
            [
                Evidence(ModelCapability.TextGeneration, expiresAt: Now.AddMinutes(-1)),
                Evidence(ModelCapability.Streaming),
            ],
        };
        var provider = DeterministicModelProvider.Create(
            descriptor,
            new DeterministicModelScript([new DeterministicTextStep("unavailable")]),
            new FixedClock()).Value;

        var events = await CollectAsync(provider.StreamAsync(
            CreateRequest(
                [new ModelMessage(ModelMessageRole.User, [new ModelTextContent("hello")])],
                []),
            CancellationToken.None));

        Assert.Single(events);
        Assert.Equal(
            ModelProviderErrorCode.UnsupportedCapability,
            Assert.IsType<ModelErrorEvent>(events[0]).Error.Code);
    }

    [Fact]
    public async Task Provider_emits_typed_retryable_failure_without_false_completion()
    {
        var provider = DeterministicModelProvider.Create(
            CreateDescriptor(),
            new DeterministicModelScript(
            [
                new DeterministicTextStep("partial"),
                new DeterministicFailureStep(new ModelProviderError(
                    ModelProviderErrorCode.RateLimited,
                    "Deterministic rate limit.",
                    true,
                    429,
                    TimeSpan.FromSeconds(2))),
            ]),
            new FixedClock()).Value;

        var events = await CollectAsync(provider.StreamAsync(
            CreateRequest(
                [new ModelMessage(ModelMessageRole.User, [new ModelTextContent("hello")])],
                []),
            CancellationToken.None));

        Assert.Collection(
            events,
            item => Assert.IsType<ModelStartedEvent>(item),
            item => Assert.IsType<ModelTextDeltaEvent>(item),
            item => Assert.True(Assert.IsType<ModelErrorEvent>(item).Error.IsRetryable));
        Assert.DoesNotContain(events, item => item is ModelCompletedEvent);
    }

    [Fact]
    public void Provider_rejects_ambiguous_or_unavailable_capability_evidence()
    {
        var baseline = CreateDescriptor();
        var invalid = new ModelProviderDescriptor[]
        {
            baseline with { ProfileId = default },
            baseline with { ProviderType = "Deterministic" },
            baseline with
            {
                Capabilities =
                [
                    Evidence(ModelCapability.TextGeneration),
                    Evidence(ModelCapability.Streaming) with
                    {
                        Availability = ModelCapabilityAvailability.Unavailable,
                    },
                ],
            },
            baseline with
            {
                Capabilities =
                [
                    Evidence(ModelCapability.TextGeneration),
                    Evidence(ModelCapability.Streaming),
                    Evidence(ModelCapability.Streaming),
                ],
            },
            baseline with
            {
                Capabilities =
                [
                    Evidence(ModelCapability.TextGeneration),
                    Evidence(ModelCapability.Streaming) with
                    {
                        ExpiresAt = Now.AddMinutes(-6),
                    },
                ],
            },
        };

        foreach (var descriptor in invalid)
        {
            var result = DeterministicModelProvider.Create(
                descriptor,
                new DeterministicModelScript([]),
                new FixedClock());
            Assert.False(result.IsSuccess);
            Assert.Equal(FailureCode.ValidationFailure, result.Failure?.Code);
        }
    }

    [Fact]
    public void Provider_rejects_malformed_unauthorized_or_ambiguous_scripts()
    {
        var baseline = CreateDescriptor();
        var invalid = new (ModelProviderDescriptor Descriptor, DeterministicModelScript Script)[]
        {
            (
                baseline,
                new DeterministicModelScript(
                [
                    new DeterministicToolCallStep("call-001", "read_file", ["{}"]),
                ])),
            (
                CreateDescriptor(ModelCapability.ToolCalls),
                new DeterministicModelScript(
                [
                    new DeterministicToolCallStep("call-001", "read_file", ["{}"]),
                    new DeterministicToolCallStep("call-001", "read_file", ["{}"]),
                ])),
            (
                CreateDescriptor(ModelCapability.ToolCalls),
                new DeterministicModelScript(
                [
                    new DeterministicToolCallStep(
                        "call-001",
                        "read_file",
                        ["{\"path\":1,\"path\":2}"]),
                ])),
            (
                baseline,
                new DeterministicModelScript(
                [
                    new DeterministicFailureStep(new ModelProviderError(
                        ModelProviderErrorCode.ProviderUnavailable,
                        "Fixture failure.",
                        true)),
                    new DeterministicTextStep("after-terminal"),
                ])),
            (
                CreateDescriptor(ModelCapability.StructuredOutput),
                new DeterministicModelScript(
                [
                    new DeterministicStructuredOutputStep("not-json"),
                ])),
        };

        foreach (var candidate in invalid)
        {
            var result = DeterministicModelProvider.Create(
                candidate.Descriptor,
                candidate.Script,
                new FixedClock());
            Assert.False(result.IsSuccess);
            Assert.Equal(FailureCode.ValidationFailure, result.Failure?.Code);
        }
    }

    [Fact]
    public void Catalog_is_exact_immutable_ordered_and_rejects_duplicate_profiles()
    {
        var first = DeterministicModelProvider.Create(
            CreateDescriptor(),
            new DeterministicModelScript([]),
            new FixedClock()).Value;
        var secondProfile = new ProviderProfileId(Guid.Parse("7a55abf0-2301-49f6-9a94-370d572e9df4"));
        var second = DeterministicModelProvider.Create(
            CreateDescriptor() with
            {
                ProfileId = secondProfile,
                ProviderType = "a-deterministic",
                Model = "deterministic-a",
            },
            new DeterministicModelScript([]),
            new FixedClock()).Value;

        var catalog = ModelProviderCatalog.Create([first, second]);
        var duplicate = ModelProviderCatalog.Create([first, first]);

        Assert.True(catalog.IsSuccess, catalog.Failure?.Message);
        Assert.Equal([secondProfile, ProfileId], catalog.Value.List().Select(item => item.ProfileId));
        Assert.Equal(ProfileId, catalog.Value.Resolve(ProfileId).Value.Descriptor.ProfileId);
        Assert.Equal(
            FailureCode.UnsupportedCapability,
            catalog.Value.Resolve(new ProviderProfileId(Guid.NewGuid())).Failure?.Code);
        Assert.Equal(FailureCode.ValidationFailure, duplicate.Failure?.Code);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<ModelProviderDescriptor>)catalog.Value.List()).Add(first.Descriptor));
    }

    private static ModelProviderDescriptor CreateDescriptor(params ModelCapability[] optionalCapabilities)
    {
        var capabilities = new List<ModelCapabilityEvidence>
        {
            Evidence(ModelCapability.TextGeneration),
            Evidence(ModelCapability.Streaming),
        };
        capabilities.AddRange(optionalCapabilities.Select(capability => Evidence(capability)));
        return new ModelProviderDescriptor(
            ProfileId,
            "deterministic",
            "deterministic-v1",
            capabilities);
    }

    private static ModelCapabilityEvidence Evidence(
        ModelCapability capability,
        DateTimeOffset? expiresAt = null) => new(
        capability,
        ModelCapabilityEvidenceSource.Declared,
        ModelCapabilityAvailability.Available,
        "Deterministic unit-test evidence.",
        Now.AddMinutes(-5),
        expiresAt);

    private static ModelRequest CreateRequest(
        IReadOnlyList<ModelMessage> messages,
        IReadOnlyList<ModelToolDefinition> tools,
        bool structured = false) => new(
        new ModelRequestId(Guid.Parse("f013c42a-bbc2-42f1-a659-9b3797af39c1")),
        "deterministic-v1",
        messages,
        tools,
        structured
            ? new ModelResponseFormat(ModelResponseFormatKind.JsonSchema, "{\"type\":\"object\"}")
            : new ModelResponseFormat(ModelResponseFormatKind.Text),
        new ModelInvocationLimits(100, 4, 32, 30),
        0,
        1,
        42,
        new CorrelationId("model-unit-test"));

    private static ModelAttachmentReference CreateAttachment(string hashCharacter = "a") => new(
        "sha256:" + new string(hashCharacter[0], 64),
        "image/png",
        1024,
        ModelAttachmentModality.Image,
        "fixture.png");

    private static async Task<List<ModelStreamEvent>> CollectAsync(
        IAsyncEnumerable<ModelStreamEvent> stream)
    {
        var events = new List<ModelStreamEvent>();
        await foreach (var item in stream)
        {
            events.Add(item);
        }

        return events;
    }

    private static async Task<ModelStartedEvent> FirstStartedAsync(
        DeterministicModelProvider provider,
        ModelRequest request)
    {
        await using var enumerator = provider.StreamAsync(request, CancellationToken.None).GetAsyncEnumerator();
        Assert.True(await enumerator.MoveNextAsync());
        return Assert.IsType<ModelStartedEvent>(enumerator.Current);
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }
}
