using AgentForge.Abstractions.Models;
using AgentForge.Abstractions.Time;
using AgentForge.Domain.Agents;
using AgentForge.Domain.Models;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Providers;
using AgentForge.Models;

namespace AgentForge.UnitTests;

public sealed class ModelRouterTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 11, 14, 0, 0, TimeSpan.Zero);
    private static readonly ProviderProfileId PrimaryId = Id("0f5d3fd4-698d-49ba-bc03-7c62a9db3ee6");
    private static readonly ProviderProfileId FirstFallbackId = Id("1feac4bf-15e8-4ca7-8ff8-5fc277905c20");
    private static readonly ProviderProfileId SecondFallbackId = Id("49fb2b8c-7774-4503-ab64-b1dc84fd7ae2");

    [Fact]
    public void Selects_viable_primary_before_better_ranked_fallback()
    {
        var router = Router(
            Descriptor(PrimaryId, reliability: 8_000, cost: 10, latency: 900),
            Descriptor(FirstFallbackId, reliability: 9_999, cost: 1, latency: 50));

        var result = router.SelectRoute(RouteRequest(PrimaryId));

        Assert.True(result.IsSuccess, result.Failure?.Message);
        Assert.Equal(PrimaryId, result.Value.ProfileId);
        Assert.False(result.Value.IsFallback);
        Assert.Equal(71, result.Value.SelectionEvidenceHash.Length);
        Assert.StartsWith("sha256:", result.Value.SelectionEvidenceHash, StringComparison.Ordinal);
    }

    [Fact]
    public void Excluded_primary_uses_reliability_then_cost_latency_and_changes_evidence()
    {
        var router = Router(
            Descriptor(PrimaryId, reliability: 9_999, cost: 0, latency: 1),
            Descriptor(FirstFallbackId, reliability: 9_500, cost: 20, latency: 500),
            Descriptor(SecondFallbackId, reliability: 9_500, cost: 5, latency: 900));
        var first = router.SelectRoute(RouteRequest(PrimaryId, excluded: [PrimaryId]));
        var second = router.SelectRoute(RouteRequest(PrimaryId, excluded: [PrimaryId, SecondFallbackId]));

        Assert.True(first.IsSuccess, first.Failure?.Message);
        Assert.Equal(SecondFallbackId, first.Value.ProfileId);
        Assert.True(first.Value.IsFallback);
        Assert.True(second.IsSuccess, second.Failure?.Message);
        Assert.Equal(FirstFallbackId, second.Value.ProfileId);
        Assert.NotEqual(first.Value.SelectionEvidenceHash, second.Value.SelectionEvidenceHash);
    }

    [Fact]
    public void Selection_evidence_binds_input_and_output_context_requirements()
    {
        var router = Router(Descriptor(PrimaryId));
        var baseline = router.SelectRoute(RouteRequest(PrimaryId));
        var changedInput = router.SelectRoute(RouteRequest(PrimaryId, estimatedInput: 101));
        var changedOutput = router.SelectRoute(RouteRequest(
            PrimaryId,
            request: Request() with { Limits = new ModelInvocationLimits(101, 0, 32, 30) }));

        Assert.True(baseline.IsSuccess, baseline.Failure?.Message);
        Assert.True(changedInput.IsSuccess, changedInput.Failure?.Message);
        Assert.True(changedOutput.IsSuccess, changedOutput.Failure?.Message);
        Assert.NotEqual(baseline.Value.SelectionEvidenceHash, changedInput.Value.SelectionEvidenceHash);
        Assert.NotEqual(baseline.Value.SelectionEvidenceHash, changedOutput.Value.SelectionEvidenceHash);
    }

    [Fact]
    public void Local_only_never_uses_cloud_and_can_select_policy_approved_local_fallback()
    {
        var router = Router(
            Descriptor(PrimaryId, location: ModelProviderDataLocation.Cloud),
            Descriptor(FirstFallbackId, location: ModelProviderDataLocation.PrivateNetwork));

        var result = router.SelectRoute(RouteRequest(
            PrimaryId,
            locality: ModelDataLocality.LocalOnly,
            allowFallback: true));

        Assert.True(result.IsSuccess, result.Failure?.Message);
        Assert.Equal(FirstFallbackId, result.Value.ProfileId);
        Assert.True(result.Value.IsFallback);

        var cloudOnly = Router(Descriptor(PrimaryId, location: ModelProviderDataLocation.Cloud))
            .SelectRoute(RouteRequest(PrimaryId, locality: ModelDataLocality.LocalOnly));
        Assert.False(cloudOnly.IsSuccess);
        Assert.Equal(FailureCode.PolicyDenied, cloudOnly.Failure?.Code);
    }

    [Fact]
    public void Missing_or_expired_route_approval_is_denied_before_context_and_tools()
    {
        var declared = Router(Descriptor(
            PrimaryId,
            routeSource: ModelCapabilityEvidenceSource.Declared,
            maximumContext: 1,
            maximumOutput: 1));
        var expired = Router(Descriptor(
            PrimaryId,
            routeExpiry: Now.AddSeconds(-1),
            maximumContext: 1,
            maximumOutput: 1));
        var request = RouteRequest(
            PrimaryId,
            request: Request() with
            {
                Tools = [Tool()],
                Limits = new ModelInvocationLimits(100, 1, 32, 30),
            },
            estimatedInput: 1_000);

        var declaredResult = declared.SelectRoute(request);
        var expiredResult = expired.SelectRoute(request);

        Assert.Equal(FailureCode.PolicyDenied, declaredResult.Failure?.Code);
        Assert.Equal(FailureCode.PolicyDenied, expiredResult.Failure?.Code);
    }

    [Fact]
    public void Context_window_and_output_bounds_fail_typed_before_tool_support()
    {
        var router = Router(Descriptor(
            PrimaryId,
            maximumContext: 1_000,
            maximumOutput: 100));
        var request = Request() with
        {
            Tools = [Tool()],
            Limits = new ModelInvocationLimits(101, 1, 32, 30),
        };

        var outputResult = router.SelectRoute(RouteRequest(PrimaryId, request: request, estimatedInput: 1));
        var contextResult = router.SelectRoute(RouteRequest(
            PrimaryId,
            request: request with { Limits = new ModelInvocationLimits(100, 1, 32, 30) },
            estimatedInput: 901));

        Assert.Equal(FailureCode.BudgetExceeded, outputResult.Failure?.Code);
        Assert.Equal(FailureCode.BudgetExceeded, contextResult.Failure?.Code);
    }

    [Fact]
    public void Tool_support_is_required_after_modality_policy_and_context_filters()
    {
        var unsupported = Router(Descriptor(PrimaryId));
        var supported = Router(Descriptor(PrimaryId, capabilities: [ModelCapability.ToolCalls]));
        var route = RouteRequest(PrimaryId, request: Request() with
        {
            Tools = [Tool()],
            Limits = new ModelInvocationLimits(100, 1, 32, 30),
        });

        var failure = unsupported.SelectRoute(route);
        var success = supported.SelectRoute(route);

        Assert.Equal(FailureCode.UnsupportedCapability, failure.Failure?.Code);
        Assert.True(success.IsSuccess, success.Failure?.Message);
        Assert.Contains(ModelCapability.ToolCalls, success.Value.RequiredCapabilities);
    }

    [Fact]
    public void Image_content_routes_only_to_capable_provider_and_is_never_omitted()
    {
        var router = Router(
            Descriptor(PrimaryId),
            Descriptor(FirstFallbackId, capabilities: [ModelCapability.ImageInput]));
        var request = Request() with
        {
            Messages =
            [
                new ModelMessage(ModelMessageRole.User,
                [
                    new ModelTextContent("Inspect this image."),
                    new ModelAttachmentContent(new ModelAttachmentReference(
                        "sha256:" + new string('a', 64),
                        "image/png",
                        128,
                        ModelAttachmentModality.Image,
                        "fixture.png")),
                ]),
            ],
        };

        var result = router.SelectRoute(RouteRequest(PrimaryId, request: request));

        Assert.True(result.IsSuccess, result.Failure?.Message);
        Assert.Equal(FirstFallbackId, result.Value.ProfileId);
        Assert.Contains(ModelCapability.ImageInput, result.Value.RequiredCapabilities);
    }

    [Fact]
    public void Disabled_fallback_and_hostile_exclusions_fail_closed()
    {
        var router = Router(
            Descriptor(PrimaryId, capabilities: [ModelCapability.ImageInput]),
            Descriptor(FirstFallbackId, capabilities: [ModelCapability.ImageInput]));
        var imageRequest = Request() with
        {
            Messages =
            [
                new ModelMessage(ModelMessageRole.User,
                [
                    new ModelAttachmentContent(new ModelAttachmentReference(
                        "sha256:" + new string('b', 64),
                        "image/png",
                        32,
                        ModelAttachmentModality.Image,
                        null)),
                ]),
            ],
        };
        var fallbackDisabled = router.SelectRoute(RouteRequest(
            PrimaryId,
            request: imageRequest,
            allowFallback: false,
            excluded: [PrimaryId]));
        var duplicateExclusion = router.SelectRoute(RouteRequest(
            PrimaryId,
            excluded: [FirstFallbackId, FirstFallbackId]));

        Assert.Equal(FailureCode.PolicyDenied, fallbackDisabled.Failure?.Code);
        Assert.Equal(FailureCode.ValidationFailure, duplicateExclusion.Failure?.Code);
    }

    private static ModelRouter Router(params ModelProviderDescriptor[] descriptors)
    {
        var providers = descriptors.Select(item => (IModelProvider)new FakeProvider(item)).ToArray();
        return new ModelRouter(ModelProviderCatalog.Create(providers).Value, new FixedClock());
    }

    private static ModelProviderDescriptor Descriptor(
        ProviderProfileId id,
        ModelProviderDataLocation location = ModelProviderDataLocation.Cloud,
        ModelCapabilityEvidenceSource routeSource = ModelCapabilityEvidenceSource.PolicyApproved,
        int reliability = 9_000,
        decimal? cost = 2,
        int latency = 200,
        int maximumContext = 8_192,
        int maximumOutput = 1_024,
        DateTimeOffset? routeExpiry = null,
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
            "route-fixture",
            "route-model",
            evidence,
            new ModelProviderRoutingEvidence(
                location,
                routeSource,
                maximumContext,
                maximumOutput,
                reliability,
                cost,
                cost,
                latency,
                Now.AddMinutes(-5),
                routeExpiry ?? Now.AddMinutes(30)));
    }

    private static ModelCapabilityEvidence Capability(ModelCapability capability) => new(
        capability,
        ModelCapabilityEvidenceSource.Probed,
        ModelCapabilityAvailability.Available,
        "Current deterministic routing evidence.",
        Now.AddMinutes(-5),
        Now.AddMinutes(30));

    private static ModelRoutingRequest RouteRequest(
        ProviderProfileId primary,
        ModelRequest? request = null,
        ModelDataLocality locality = ModelDataLocality.CloudAllowed,
        bool allowFallback = true,
        long estimatedInput = 100,
        IReadOnlyList<ProviderProfileId>? excluded = null) => new(
        request ?? Request(),
        new AgentModelPolicy(primary, locality, allowFallback),
        estimatedInput,
        excluded ?? []);

    private static ModelRequest Request() => new(
        new ModelRequestId(Guid.Parse("057867c1-d3d2-40e7-9244-c5ab475ab6aa")),
        "route-model",
        [new ModelMessage(ModelMessageRole.User, [new ModelTextContent("route this request")])],
        [],
        new ModelResponseFormat(ModelResponseFormatKind.Text),
        new ModelInvocationLimits(100, 0, 32, 30),
        0,
        1,
        42,
        new CorrelationId("model-router-unit"));

    private static ModelToolDefinition Tool() => new(
        "read_file",
        "Read one deterministic fixture file.",
        "{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"}}}");

    private static ProviderProfileId Id(string value) => new(Guid.Parse(value));

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
