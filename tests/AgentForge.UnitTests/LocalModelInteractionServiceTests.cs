using System.Runtime.CompilerServices;
using AgentForge.Abstractions.Models;
using AgentForge.Abstractions.Time;
using AgentForge.Domain.Models;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Providers;
using AgentForge.Domain.Security;
using AgentForge.Models;

namespace AgentForge.UnitTests;

public sealed class LocalModelInteractionServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 12, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Invocation_returns_bounded_text_receipt_without_exposing_tools()
    {
        var provider = new ScriptedProvider(static (request, cancellationToken) =>
            SuccessfulStream(request, cancellationToken));
        var service = new LocalModelInteractionService(new FakeFactory(provider));
        var observer = new RecordingObserver();

        var result = await service.InvokeAsync(Request(), observer, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("AgentForge local model test passed.", result.Value.Text);
        Assert.Equal(new ModelUsage(12, 7, 0, null, null), result.Value.Usage);
        Assert.Equal(ModelFinishReason.Stop, result.Value.FinishReason);
        Assert.Equal(4, result.Value.EventCount);
        Assert.StartsWith("sha256:", result.Value.EvidenceHash, StringComparison.Ordinal);
        Assert.NotNull(provider.ObservedRequest);
        Assert.Empty(provider.ObservedRequest!.Tools);
        Assert.Equal(ModelResponseFormatKind.Text, provider.ObservedRequest.ResponseFormat.Kind);
        Assert.Equal(2, provider.ObservedRequest.Messages.Count);
        Assert.Equal(ModelMessageRole.System, provider.ObservedRequest.Messages[0].Role);
        Assert.Equal(ModelMessageRole.User, provider.ObservedRequest.Messages[1].Role);
        Assert.Collection(observer.Events,
            item => Assert.Equal(LocalModelInteractionProgressKind.Started, item.Kind),
            item =>
            {
                Assert.Equal(LocalModelInteractionProgressKind.TextDelta, item.Kind);
                Assert.Equal("AgentForge local model test passed.", item.TextDelta);
            },
            item =>
            {
                Assert.Equal(LocalModelInteractionProgressKind.Usage, item.Kind);
                Assert.Equal(new ModelUsage(12, 7, 0, null, null), item.Usage);
            });
    }

    [Fact]
    public async Task Invocation_accepts_scaled_local_output_and_wall_clock_bounds()
    {
        var provider = new ScriptedProvider(static (request, cancellationToken) =>
            SuccessfulStream(request, cancellationToken));
        var service = new LocalModelInteractionService(new FakeFactory(provider));
        var request = Request() with
        {
            Limits = new ModelInvocationLimits(32_768, 0, 33_280, 270),
        };

        var result = await service.InvokeAsync(request, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.NotNull(provider.ObservedRequest);
        Assert.Equal(32_768, provider.ObservedRequest!.Limits.MaximumOutputTokens);
        Assert.Equal(33_280, provider.ObservedRequest.Limits.MaximumEvents);
        Assert.Equal(270, provider.ObservedRequest.Limits.MaximumWallClockSeconds);
    }

    [Fact]
    public async Task Invocation_denies_tool_calls_even_when_a_provider_emits_one()
    {
        var provider = new ScriptedProvider(static (request, cancellationToken) =>
            ToolCallStream(request, cancellationToken));
        var service = new LocalModelInteractionService(new FakeFactory(provider));

        var result = await service.InvokeAsync(Request(), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(FailureCode.PolicyDenied, result.Failure!.Code);
    }

    [Fact]
    public async Task Invocation_rejects_an_oversized_provider_response()
    {
        var provider = new ScriptedProvider(static (request, cancellationToken) =>
            OversizedStream(request, cancellationToken));
        var service = new LocalModelInteractionService(new FakeFactory(provider));

        var result = await service.InvokeAsync(Request(), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(FailureCode.BudgetExceeded, result.Failure!.Code);
    }

    [Fact]
    public void Provider_factory_allows_only_credential_free_loopback_or_private_routes()
    {
        var factory = new LocalModelProviderFactory(new PassthroughContextPreparer(), new FixedClock());

        var publicRoute = factory.Create(Profile(new Uri("https://api.example.test/v1")));
        var credentialedRoute = factory.Create(Profile(
            new Uri("http://192.168.1.89:8000/v1"),
            new SecretReference("test-store", "credential")));
        var privateRoute = factory.Create(Profile(new Uri("http://192.168.1.89:8000/v1")));

        Assert.False(publicRoute.IsSuccess);
        Assert.Equal(FailureCode.PolicyDenied, publicRoute.Failure!.Code);
        Assert.False(credentialedRoute.IsSuccess);
        Assert.Equal(FailureCode.UnsupportedCapability, credentialedRoute.Failure!.Code);
        Assert.True(privateRoute.IsSuccess);
        Assert.Equal(ModelProviderDataLocation.PrivateNetwork, privateRoute.Value.Descriptor.Routing!.DataLocation);
        Assert.Equal(262_144, privateRoute.Value.Descriptor.Routing.MaximumOutputTokens);
        (privateRoute.Value as IDisposable)?.Dispose();
    }

    private static LocalModelInteractionRequest Request() => new(
        new ModelRequestId(Guid.Parse("7fcac080-01ed-45c4-879f-95cbac35d3fb")),
        Profile(new Uri("http://192.168.1.89:8000/v1")),
        "Remain within the bounded test policy.",
        "Reply with a short confirmation.",
        new ModelInvocationLimits(256, 0, 32, 30),
        new CorrelationId("correlation"));

    private static ProviderProfile Profile(Uri endpoint, SecretReference? secretReference = null) =>
        new(
            new ProviderProfileId(Guid.Parse("6f4e6bf7-f19d-46d9-b340-b758a6fb53a2")),
            new InstallationId(Guid.Parse("958c36c8-e382-4ad1-b868-948dafab7b76")),
            "primary",
            "openai-compatible",
            endpoint,
            "qwen3.6",
            secretReference ?? SecretReference.NoCredential,
            new ProviderCapabilitySummary(true, true, false, false, "test"),
            1,
            Now,
            Now,
            new ActorId("operator"),
            new CorrelationId("correlation"));

    private static async IAsyncEnumerable<ModelStreamEvent> SuccessfulStream(
        ModelRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Task.CompletedTask;
        yield return new ModelStartedEvent(
            request.Id,
            0,
            Now,
            new ProviderProfileId(Guid.Parse("6f4e6bf7-f19d-46d9-b340-b758a6fb53a2")),
            "openai-compatible",
            "qwen3.6",
            "sha256:input",
            "sha256:capabilities",
            0,
            "redacted");
        yield return new ModelTextDeltaEvent(request.Id, 1, Now, "AgentForge local model test passed.");
        yield return new ModelUsageEvent(request.Id, 2, Now, new ModelUsage(12, 7, 0, null, null));
        yield return new ModelCompletedEvent(request.Id, 3, Now, ModelFinishReason.Stop);
    }

    private static async IAsyncEnumerable<ModelStreamEvent> ToolCallStream(
        ModelRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Task.CompletedTask;
        yield return new ModelStartedEvent(
            request.Id,
            0,
            Now,
            new ProviderProfileId(Guid.Parse("6f4e6bf7-f19d-46d9-b340-b758a6fb53a2")),
            "openai-compatible",
            "qwen3.6",
            "sha256:input",
            "sha256:capabilities");
        yield return new ModelToolCallCompletedEvent(request.Id, 1, Now, "call-1", "shell", "{}");
    }

    private static async IAsyncEnumerable<ModelStreamEvent> OversizedStream(
        ModelRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Task.CompletedTask;
        yield return new ModelStartedEvent(
            request.Id,
            0,
            Now,
            new ProviderProfileId(Guid.Parse("6f4e6bf7-f19d-46d9-b340-b758a6fb53a2")),
            "openai-compatible",
            "qwen3.6",
            "sha256:input",
            "sha256:capabilities");
        yield return new ModelTextDeltaEvent(request.Id, 1, Now, new string('x', 32_769));
    }

    private sealed class FakeFactory(IModelProvider provider) : ILocalModelProviderFactory
    {
        public DomainResult<IModelProvider> Create(ProviderProfile profile) => DomainResult.Success(provider);
    }

    private sealed class PassthroughContextPreparer : IModelContextPreparer
    {
        public DomainResult<PreparedModelContext> Prepare(ModelRequest request) => DomainResult.Success(
            new PreparedModelContext(request, 0, "test", "sha256:input"));
    }

    private sealed class RecordingObserver : ILocalModelInteractionObserver
    {
        public List<LocalModelInteractionProgress> Events { get; } = [];

        public ValueTask OnProgressAsync(
            LocalModelInteractionProgress progress,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add(progress);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    private sealed class ScriptedProvider(
        Func<ModelRequest, CancellationToken, IAsyncEnumerable<ModelStreamEvent>> stream) : IModelProvider
    {
        public ModelRequest? ObservedRequest { get; private set; }

        public ModelProviderDescriptor Descriptor { get; } = new(
            new ProviderProfileId(Guid.Parse("6f4e6bf7-f19d-46d9-b340-b758a6fb53a2")),
            "openai-compatible",
            "qwen3.6",
            [],
            null);

        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            CancellationToken cancellationToken)
        {
            ObservedRequest = request;
            return stream(request, cancellationToken);
        }
    }
}
