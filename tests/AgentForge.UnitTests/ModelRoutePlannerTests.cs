using System.Collections.ObjectModel;
using AgentForge.Abstractions.Models;
using AgentForge.Abstractions.Time;
using AgentForge.Domain.Agents;
using AgentForge.Domain.Installations;
using AgentForge.Domain.Models;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Providers;
using AgentForge.Domain.Security;
using AgentForge.Models;

namespace AgentForge.UnitTests;

public sealed class ModelRoutePlannerTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 11, 16, 0, 0, TimeSpan.Zero);
    private static readonly InstallationId InstallationId = new(Guid.Parse("b0ee6149-aae1-48ea-ae02-4787c27f21a0"));
    private static readonly AgentIdentityId AgentId = new(Guid.Parse("3f88148f-bc2f-4e11-a482-fb35eef3cf52"));
    private static readonly ProviderProfileId PrimaryId = Id("2533579a-56c7-4dd2-b39e-f7cd25ba7bf5");
    private static readonly ProviderProfileId FallbackId = Id("f826a6b4-509c-4893-928d-c0060d490f87");

    [Fact]
    public async Task Plans_prepared_primary_against_two_stable_authority_and_health_reads()
    {
        var authority = Authority(Profile(PrimaryId));
        var authorityReader = new SequenceAuthorityReader(authority);
        var healthSource = new SequenceHealthSource([Healthy(PrimaryId)]);
        var preparer = new RecordingPreparer(redact: true);
        var planner = Planner(
            authorityReader,
            healthSource,
            preparer,
            Descriptor(PrimaryId));
        var original = Request("Bearer abcdefghijklmnopqrstuvwxyz");

        var result = await planner.PlanAsync(PlanningRequest(original), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Failure?.Message);
        Assert.Equal(PrimaryId, result.Value.Route.ProfileId);
        Assert.False(result.Value.Route.IsFallback);
        Assert.Equal(7, result.Value.InstallationVersion);
        Assert.Equal(3, result.Value.AgentVersion);
        Assert.Equal(4, result.Value.ProviderVersion);
        Assert.Equal(1, result.Value.ContextRedactionCount);
        Assert.Equal("test-context-v1", result.Value.ContextPreparationPolicy);
        Assert.Equal(71, result.Value.PreparedInputHash.Length);
        Assert.Equal(71, result.Value.HealthEvidenceHash.Length);
        Assert.Equal(71, result.Value.PlanEvidenceHash.Length);
        Assert.InRange(result.Value.ValidUntil, Now.AddTicks(1), Now.AddSeconds(5));
        Assert.Equal(
            "Bearer abcdefghijklmnopqrstuvwxyz",
            Assert.IsType<ModelTextContent>(original.Messages[0].Content[0]).Text);
        Assert.Equal(2, authorityReader.ReadCount);
        Assert.Equal(2, healthSource.ReadCount);
        Assert.Equal(1, preparer.CallCount);
    }

    [Fact]
    public async Task Temporary_primary_failure_selects_healthy_fallback_without_weakening_locality()
    {
        var authority = Authority(Profile(PrimaryId), Profile(FallbackId));
        authority = authority with
        {
            Agent = authority.Agent with
            {
                ModelPolicy = new AgentModelPolicy(PrimaryId, ModelDataLocality.LocalOnly, true),
            },
        };
        var planner = Planner(
            new SequenceAuthorityReader(authority),
            new SequenceHealthSource([
                Unavailable(PrimaryId),
                Healthy(FallbackId),
            ]),
            new RecordingPreparer(),
            Descriptor(PrimaryId, ModelProviderDataLocation.Cloud),
            Descriptor(FallbackId, ModelProviderDataLocation.PrivateNetwork));

        var result = await planner.PlanAsync(PlanningRequest(Request()), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Failure?.Message);
        Assert.Equal(FallbackId, result.Value.Route.ProfileId);
        Assert.True(result.Value.Route.IsFallback);
    }

    [Fact]
    public async Task Attempt_history_is_bounded_unique_and_must_match_the_current_catalog()
    {
        var authorityReader = new SequenceAuthorityReader(Authority(Profile(PrimaryId), Profile(FallbackId)));
        var health = new SequenceHealthSource([Healthy(PrimaryId), Healthy(FallbackId)]);
        var planner = Planner(
            authorityReader,
            health,
            new RecordingPreparer(),
            Descriptor(PrimaryId),
            Descriptor(FallbackId));
        var duplicate = PlanningRequest(Request()) with
        {
            AttemptedProfileIds = [PrimaryId, PrimaryId],
        };
        var unknown = PlanningRequest(Request()) with
        {
            AttemptedProfileIds = [Id("ea9d066c-99a3-4ab6-b03d-63a92e54aa60")],
        };

        var duplicateResult = await planner.PlanAsync(duplicate, CancellationToken.None);
        var unknownResult = await planner.PlanAsync(unknown, CancellationToken.None);

        Assert.Equal(FailureCode.ValidationFailure, duplicateResult.Failure?.Code);
        Assert.Equal(FailureCode.ConcurrencyConflict, unknownResult.Failure?.Code);
        Assert.Equal(1, authorityReader.ReadCount);
        Assert.Equal(0, health.ReadCount);
    }

    [Theory]
    [InlineData(ModelProviderHealthStatus.Unknown)]
    [InlineData(ModelProviderHealthStatus.TemporarilyUnavailable)]
    public async Task Missing_unknown_or_unavailable_health_fails_retryable(
        ModelProviderHealthStatus status)
    {
        var evidence = status is ModelProviderHealthStatus.Unknown
            ? new ModelProviderHealthEvidence(
                PrimaryId,
                status,
                ModelHealthEvidenceSource.Observed,
                0,
                "health-unknown",
                Now.AddMinutes(-1),
                Now.AddMinutes(1))
            : Unavailable(PrimaryId);
        var planner = Planner(
            new SequenceAuthorityReader(Authority(Profile(PrimaryId))),
            new SequenceHealthSource([evidence]),
            new RecordingPreparer(),
            Descriptor(PrimaryId));

        var result = await planner.PlanAsync(PlanningRequest(Request()), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(FailureCode.RecoverableExternalFailure, result.Failure?.Code);
        Assert.True(result.Failure?.IsRetryable);
    }

    [Fact]
    public async Task Missing_health_and_expired_health_are_never_treated_as_healthy()
    {
        var authority = Authority(Profile(PrimaryId));
        var missing = Planner(
            new SequenceAuthorityReader(authority),
            new SequenceHealthSource([]),
            new RecordingPreparer(),
            Descriptor(PrimaryId));
        var expired = Planner(
            new SequenceAuthorityReader(authority),
            new SequenceHealthSource([
                Healthy(PrimaryId) with
                {
                    ObservedAt = Now.AddMinutes(-2),
                    ExpiresAt = Now.AddTicks(-1),
                },
            ]),
            new RecordingPreparer(),
            Descriptor(PrimaryId));

        var missingResult = await missing.PlanAsync(PlanningRequest(Request()), CancellationToken.None);
        var expiredResult = await expired.PlanAsync(PlanningRequest(Request()), CancellationToken.None);

        Assert.Equal(FailureCode.RecoverableExternalFailure, missingResult.Failure?.Code);
        Assert.Equal(FailureCode.RecoverableExternalFailure, expiredResult.Failure?.Code);
    }

    [Fact]
    public async Task Ready_versions_and_current_agent_budgets_fail_closed_before_health()
    {
        var notReady = Authority(Profile(PrimaryId)) with
        {
            Installation = Installation() with { State = InstallationState.Configuring },
        };
        var tightBudget = Authority(Profile(PrimaryId)) with
        {
            Agent = Agent() with
            {
                Budget = new AgentBudget(4, 0, 50, 50, 5),
            },
        };
        var health = new SequenceHealthSource([Healthy(PrimaryId)]);
        var notReadyPlanner = Planner(
            new SequenceAuthorityReader(notReady),
            health,
            new RecordingPreparer(),
            Descriptor(PrimaryId));
        var budgetPlanner = Planner(
            new SequenceAuthorityReader(tightBudget),
            health,
            new RecordingPreparer(),
            Descriptor(PrimaryId));

        var notReadyResult = await notReadyPlanner.PlanAsync(PlanningRequest(Request()), CancellationToken.None);
        var budgetResult = await budgetPlanner.PlanAsync(PlanningRequest(Request()), CancellationToken.None);
        var staleResult = await budgetPlanner.PlanAsync(
            PlanningRequest(Request()) with { ExpectedAgentVersion = 2 },
            CancellationToken.None);

        Assert.Equal(FailureCode.InvalidStateTransition, notReadyResult.Failure?.Code);
        Assert.Equal(FailureCode.BudgetExceeded, budgetResult.Failure?.Code);
        Assert.Equal(FailureCode.ConcurrencyConflict, staleResult.Failure?.Code);
        Assert.Equal(0, health.ReadCount);
    }

    [Fact]
    public async Task Request_model_cannot_select_outside_the_current_primary_model_policy()
    {
        var health = new SequenceHealthSource([Healthy(PrimaryId)]);
        var planner = Planner(
            new SequenceAuthorityReader(Authority(Profile(PrimaryId))),
            health,
            new RecordingPreparer(),
            Descriptor(PrimaryId));
        var changedModel = Request() with { Model = "model-controlled-substitution" };

        var result = await planner.PlanAsync(PlanningRequest(changedModel), CancellationToken.None);

        Assert.Equal(FailureCode.PolicyDenied, result.Failure?.Code);
        Assert.Equal(0, health.ReadCount);
    }

    [Fact]
    public async Task Catalog_and_durable_profile_mismatch_fails_as_concurrent_configuration()
    {
        var wrongType = Profile(PrimaryId) with { ProviderType = "substituted-provider" };
        var missingImagePermission = Profile(PrimaryId, images: false);
        var typePlanner = Planner(
            new SequenceAuthorityReader(Authority(wrongType)),
            new SequenceHealthSource([Healthy(PrimaryId)]),
            new RecordingPreparer(),
            Descriptor(PrimaryId));
        var imagePlanner = Planner(
            new SequenceAuthorityReader(Authority(missingImagePermission)),
            new SequenceHealthSource([Healthy(PrimaryId)]),
            new RecordingPreparer(),
            Descriptor(PrimaryId, capabilities: [ModelCapability.ImageInput]));

        var typeResult = await typePlanner.PlanAsync(PlanningRequest(Request()), CancellationToken.None);
        var imageResult = await imagePlanner.PlanAsync(
            PlanningRequest(ImageRequest()),
            CancellationToken.None);

        Assert.Equal(FailureCode.ConcurrencyConflict, typeResult.Failure?.Code);
        Assert.Equal(FailureCode.ConcurrencyConflict, imageResult.Failure?.Code);
    }

    [Fact]
    public async Task Authority_or_health_change_between_reads_invalidates_the_plan()
    {
        var initial = Authority(Profile(PrimaryId));
        var changed = initial with
        {
            Agent = initial.Agent with
            {
                Version = 4,
                ModelPolicy = new AgentModelPolicy(PrimaryId, ModelDataLocality.LocalOnly, false),
            },
        };
        var authorityPlanner = Planner(
            new SequenceAuthorityReader(initial, changed),
            new SequenceHealthSource([Healthy(PrimaryId)]),
            new RecordingPreparer(),
            Descriptor(PrimaryId));
        var healthPlanner = Planner(
            new SequenceAuthorityReader(initial),
            new SequenceHealthSource(
                [Healthy(PrimaryId)],
                [Unavailable(PrimaryId)]),
            new RecordingPreparer(),
            Descriptor(PrimaryId));

        var authorityResult = await authorityPlanner.PlanAsync(
            PlanningRequest(Request()),
            CancellationToken.None);
        var healthResult = await healthPlanner.PlanAsync(
            PlanningRequest(Request()),
            CancellationToken.None);

        Assert.Equal(FailureCode.ConcurrencyConflict, authorityResult.Failure?.Code);
        Assert.Equal(FailureCode.RecoverableExternalFailure, healthResult.Failure?.Code);
        Assert.True(healthResult.Failure?.IsRetryable);
    }

    [Fact]
    public async Task Unpersisted_audio_and_structured_output_authority_remain_closed()
    {
        var authority = Authority(Profile(PrimaryId));
        var audioPlanner = Planner(
            new SequenceAuthorityReader(authority),
            new SequenceHealthSource([Healthy(PrimaryId)]),
            new RecordingPreparer(),
            Descriptor(PrimaryId, capabilities: [ModelCapability.AudioInput]));
        var structuredPlanner = Planner(
            new SequenceAuthorityReader(authority),
            new SequenceHealthSource([Healthy(PrimaryId)]),
            new RecordingPreparer(),
            Descriptor(PrimaryId, capabilities: [ModelCapability.StructuredOutput]));

        var audioResult = await audioPlanner.PlanAsync(
            PlanningRequest(AudioRequest()),
            CancellationToken.None);
        var structuredResult = await structuredPlanner.PlanAsync(
            PlanningRequest(Request() with
            {
                ResponseFormat = new ModelResponseFormat(ModelResponseFormatKind.JsonObject),
            }),
            CancellationToken.None);

        Assert.Equal(FailureCode.UnsupportedCapability, audioResult.Failure?.Code);
        Assert.Equal(FailureCode.UnsupportedCapability, structuredResult.Failure?.Code);
    }

    private static ModelRoutePlanner Planner(
        IModelRouteAuthoritySnapshotReader authorityReader,
        IModelProviderHealthSource healthSource,
        IModelContextPreparer preparer,
        params ModelProviderDescriptor[] descriptors)
    {
        var providerCatalog = ModelProviderCatalog.Create(
            descriptors.Select(item => (IModelProvider)new FakeProvider(item))).Value;
        var clock = new FixedClock();
        return new ModelRoutePlanner(
            authorityReader,
            healthSource,
            providerCatalog,
            preparer,
            new ModelRouter(providerCatalog, clock),
            clock);
    }

    private static ModelRoutePlanningRequest PlanningRequest(ModelRequest request) => new(
        InstallationId,
        7,
        AgentId,
        3,
        request,
        100,
        []);

    private static ModelRouteAuthoritySnapshot Authority(params ProviderProfile[] profiles) => new(
        Installation(),
        Agent(),
        new ReadOnlyCollection<ProviderProfile>(profiles));

    private static InstallationSnapshot Installation() => new(
        InstallationId,
        InstallationState.Ready,
        7,
        Now.AddMinutes(-10),
        new ActorId("operator"),
        new CorrelationId("route-authority"),
        null);

    private static AgentIdentity Agent() => new(
        AgentId,
        InstallationId,
        "planner-agent",
        null,
        null,
        "en",
        "Europe/Kiev",
        "concise",
        null,
        new AgentModelPolicy(PrimaryId, ModelDataLocality.CloudAllowed, true),
        new AgentMemoryPolicy(AgentMemoryScope.Task, 30),
        new AgentCapabilityPolicy(NetworkPosture.Denied, [], []),
        new AgentBudget(8, 8, 4_096, 1_024, 60),
        new ChildAgentLimits(1, 1, 1, 1_024),
        new AgentLearningPolicy(LearningMode.Off, MutableSkillScope.None),
        3,
        Now.AddDays(-1),
        Now.AddMinutes(-10),
        new ActorId("operator"),
        new CorrelationId("route-agent"));

    private static ProviderProfile Profile(
        ProviderProfileId id,
        bool tools = false,
        bool images = false) => new(
        id,
        InstallationId,
        $"provider-{id.Value:N}",
        "planner-fixture",
        new Uri("https://models.example.test/v1/chat/completions"),
        "planner-model",
        new SecretReference("fixture", $"provider/{id.Value:N}"),
        new ProviderCapabilitySummary(true, true, tools, images, "probed"),
        4,
        Now.AddDays(-1),
        Now.AddMinutes(-10),
        new ActorId("operator"),
        new CorrelationId("route-provider"));

    private static ModelProviderDescriptor Descriptor(
        ProviderProfileId id,
        ModelProviderDataLocation location = ModelProviderDataLocation.Cloud,
        params ModelCapability[] capabilities)
    {
        var evidence = new List<ModelCapabilityEvidence>
        {
            Capability(ModelCapability.TextGeneration),
            Capability(ModelCapability.Streaming),
        };
        evidence.AddRange(capabilities.Select(Capability));
        return new ModelProviderDescriptor(
            id,
            "planner-fixture",
            "planner-model",
            evidence,
            new ModelProviderRoutingEvidence(
                location,
                ModelCapabilityEvidenceSource.PolicyApproved,
                8_192,
                1_024,
                9_500,
                1,
                2,
                200,
                Now.AddMinutes(-5),
                Now.AddMinutes(5)));
    }

    private static ModelCapabilityEvidence Capability(ModelCapability capability) => new(
        capability,
        ModelCapabilityEvidenceSource.Probed,
        ModelCapabilityAvailability.Available,
        "Current planner fixture evidence.",
        Now.AddMinutes(-5),
        Now.AddMinutes(5));

    private static ModelProviderHealthEvidence Healthy(ProviderProfileId profileId) => new(
        profileId,
        ModelProviderHealthStatus.Healthy,
        ModelHealthEvidenceSource.Probed,
        0,
        "probe-ok",
        Now.AddMinutes(-1),
        Now.AddMinutes(1));

    private static ModelProviderHealthEvidence Unavailable(ProviderProfileId profileId) => new(
        profileId,
        ModelProviderHealthStatus.TemporarilyUnavailable,
        ModelHealthEvidenceSource.Observed,
        2,
        "transport-failure",
        Now.AddMinutes(-1),
        Now.AddMinutes(1),
        Now.AddSeconds(10));

    private static ModelRequest Request(string text = "plan this request") => new(
        new ModelRequestId(Guid.Parse("539d842e-e013-42cb-bda8-923398893077")),
        "planner-model",
        [new ModelMessage(ModelMessageRole.User, [new ModelTextContent(text)])],
        [],
        new ModelResponseFormat(ModelResponseFormatKind.Text),
        new ModelInvocationLimits(100, 0, 32, 30),
        0,
        1,
        42,
        new CorrelationId("route-plan"));

    private static ModelRequest ImageRequest() => Request() with
    {
        Messages =
        [
            new ModelMessage(ModelMessageRole.User,
            [
                new ModelAttachmentContent(new ModelAttachmentReference(
                    "sha256:" + new string('a', 64),
                    "image/png",
                    32,
                    ModelAttachmentModality.Image,
                    "fixture.png")),
            ]),
        ],
    };

    private static ModelRequest AudioRequest() => Request() with
    {
        Messages =
        [
            new ModelMessage(ModelMessageRole.User,
            [
                new ModelAttachmentContent(new ModelAttachmentReference(
                    "sha256:" + new string('b', 64),
                    "audio/wav",
                    128,
                    ModelAttachmentModality.Audio,
                    "fixture.wav")),
            ]),
        ],
    };

    private static ProviderProfileId Id(string value) => new(Guid.Parse(value));

    private sealed class SequenceAuthorityReader(params ModelRouteAuthoritySnapshot[] snapshots)
        : IModelRouteAuthoritySnapshotReader
    {
        private readonly Queue<ModelRouteAuthoritySnapshot> _snapshots = new(snapshots);
        private ModelRouteAuthoritySnapshot? _last;

        public int ReadCount { get; private set; }

        public Task<DomainResult<ModelRouteAuthoritySnapshot>> ReadAsync(
            InstallationId installationId,
            AgentIdentityId agentId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCount++;
            if (_snapshots.Count > 0)
            {
                _last = _snapshots.Dequeue();
            }

            return Task.FromResult(DomainResult.Success(_last!));
        }
    }

    private sealed class SequenceHealthSource(params ModelProviderHealthEvidence[][] snapshots)
        : IModelProviderHealthSource
    {
        private readonly Queue<ModelProviderHealthEvidence[]> _snapshots = new(snapshots);
        private ModelProviderHealthEvidence[] _last = [];

        public int ReadCount { get; private set; }

        public ValueTask<DomainResult<IReadOnlyList<ModelProviderHealthEvidence>>> ReadAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCount++;
            if (_snapshots.Count > 0)
            {
                _last = _snapshots.Dequeue();
            }

            return ValueTask.FromResult(DomainResult.Success<IReadOnlyList<ModelProviderHealthEvidence>>(_last));
        }
    }

    private sealed class RecordingPreparer(bool redact = false) : IModelContextPreparer
    {
        public int CallCount { get; private set; }

        public DomainResult<PreparedModelContext> Prepare(ModelRequest request)
        {
            CallCount++;
            if (!redact)
            {
                return DomainResult.Success(new PreparedModelContext(request, 0, "test-context-v1"));
            }

            var prepared = request with
            {
                Messages =
                [
                    new ModelMessage(ModelMessageRole.User, [new ModelTextContent("[REDACTED]")]),
                ],
            };
            return DomainResult.Success(new PreparedModelContext(prepared, 1, "test-context-v1"));
        }
    }

    private sealed class FakeProvider(ModelProviderDescriptor descriptor) : IModelProvider
    {
        public ModelProviderDescriptor Descriptor { get; } = descriptor;

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }
}
