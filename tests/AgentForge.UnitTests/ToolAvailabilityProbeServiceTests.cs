using System.Text;
using AgentForge.Abstractions.Security;
using AgentForge.Abstractions.Tools;
using AgentForge.Domain.Agents;
using AgentForge.Domain.Auditing;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Security;
using AgentForge.Domain.Tools;
using AgentForge.Tools;

namespace AgentForge.UnitTests;

public sealed class ToolAvailabilityProbeServiceTests
{
    private const string Hash =
        "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public async Task Probe_exposes_only_bounded_printable_redacted_non_replay_summaries()
    {
        var descriptor = await CreateDescriptorAsync();
        var catalog = new StubCatalog(descriptor);
        var invocations = new StubInvocationService(CreateResult(Encoding.UTF8.GetBytes("fixture 1.2.3\nignored")));
        var service = new ToolAvailabilityProbeService(catalog, invocations, new MarkerRedactor());
        var request = CreateRequest();

        var ordinary = await service.ProbeAsync(request, CancellationToken.None);
        Assert.True(ordinary.IsSuccess, ordinary.Failure?.Message);
        Assert.Equal("fixture 1.2.3", ordinary.Value.ObservedSummary);
        Assert.False(ordinary.Value.SummaryWasRedacted);
        Assert.False(ordinary.Value.SummaryWasTruncated);
        Assert.Empty(invocations.LastRequest!.Parameters);

        invocations.Result = CreateResult(Encoding.UTF8.GetBytes(new string('x', 600)));
        var truncated = await service.ProbeAsync(
            request with { IdempotencyKey = "probe-truncated" },
            CancellationToken.None);
        Assert.Equal(512, truncated.Value.ObservedSummary?.Length);
        Assert.True(truncated.Value.SummaryWasTruncated);

        invocations.Result = CreateResult(Encoding.UTF8.GetBytes(new string('x', 520) + "secret-marker"));
        var redacted = await service.ProbeAsync(
            request with { IdempotencyKey = "probe-redacted" },
            CancellationToken.None);
        Assert.Null(redacted.Value.ObservedSummary);
        Assert.True(redacted.Value.SummaryWasRedacted);
        Assert.True(redacted.Value.SummaryWasTruncated);

        invocations.Result = CreateResult(Encoding.UTF8.GetBytes("redactor-boundary"));
        var redactorBound = await service.ProbeAsync(
            request with { IdempotencyKey = "probe-redactor-boundary" },
            CancellationToken.None);
        Assert.Null(redactorBound.Value.ObservedSummary);
        Assert.True(redactorBound.Value.SummaryWasRedacted);

        invocations.Result = CreateResult([0xff, 0xfe, 0xfd]);
        var invalidUtf8 = await service.ProbeAsync(
            request with { IdempotencyKey = "probe-invalid-utf8" },
            CancellationToken.None);
        Assert.Null(invalidUtf8.Value.ObservedSummary);

        invocations.Result = CreateResult(Encoding.UTF8.GetBytes("fixture 1.2.3"), replay: true);
        var replay = await service.ProbeAsync(request, CancellationToken.None);
        Assert.True(replay.Value.IsIdempotentReplay);
        Assert.Null(replay.Value.ObservedSummary);
    }

    private static async Task<ToolDescriptor> CreateDescriptorAsync()
    {
        var definition = new ToolDescriptorDefinition(
            "tool:fixture.availability",
            "1.0.0",
            "Fixture availability",
            "Checks fixture availability.",
            "Runs a deterministic fixture probe.",
            "tool:availability.probe",
            CapabilityRiskClass.Inventory,
            AuthorizationTargetKind.None,
            null,
            ToolSideEffectKind.None,
            ToolOutputSensitivity.LocalMetadata,
            [],
            new ToolProcessDefinition(
                Path.Combine(Path.GetTempPath(), "agentforge-probe"),
                ["--version"],
                [],
                [],
                ProcessSandboxKind.Container,
                ProcessNetworkPolicy.Denied,
                ProcessIsolationFeature.DirectExecutable |
                    ProcessIsolationFeature.ArgumentArray |
                    ProcessIsolationFeature.NetworkIsolation,
                5,
                4096),
            new ToolProvenance(
                ToolCatalogSourceKind.BuiltIn,
                ToolTrustLevel.BuiltIn,
                "agentforge.tests",
                "1.0.0",
                Hash),
            ToolOperationKind.AvailabilityProbe);
        var descriptor = await ToolCatalog.Create([definition]).Value.DescribeAsync(
            definition.Id,
            definition.Version,
            CancellationToken.None);
        return descriptor.Value;
    }

    private static ToolAvailabilityProbeRequest CreateRequest() => new(
        1,
        new AgentIdentityId(Guid.Parse("e2ce4221-a4a1-40df-91c4-198312812e4d")),
        0,
        new ActorId("worker"),
        "tool:fixture.availability",
        "1.0.0",
        Path.GetTempPath(),
        "probe-001",
        new CorrelationId("probe-correlation"));

    private static ToolInvocationResult CreateResult(byte[] output, bool replay = false)
    {
        var now = new DateTimeOffset(2026, 8, 11, 8, 0, 0, TimeSpan.Zero);
        var context = new AuthorizationContext(
            new InstallationId(Guid.Parse("2d7a31be-b0f5-48c2-9624-c2123b63b33a")),
            1,
            new AgentIdentityId(Guid.Parse("e2ce4221-a4a1-40df-91c4-198312812e4d")),
            0,
            new ActorId("worker"),
            "tool:availability.probe",
            CapabilityRiskClass.Inventory,
            "tool:fixture.availability",
            "1.0.0",
            Hash,
            "{}",
            Hash,
            AuthorizationTargetKind.None,
            null,
            Hash,
            Path.GetTempPath(),
            Hash,
            new CorrelationId("probe-correlation"),
            null,
            Hash);
        var authorized = ToolInvocationStateMachine.Authorize(
            new ToolInvocationId(Guid.Parse("797e8404-acd0-4d17-b3c8-49c84658b616")),
            context,
            Hash,
            null,
            "probe-001",
            now).Value;
        var capabilities = new ProcessSandboxCapabilities(
            ProcessSandboxKind.Container,
            true,
            ProcessIsolationFeature.NetworkIsolation,
            "fixture");
        var completed = ToolInvocationStateMachine.Complete(
            authorized,
            new ProcessExecutionResult(0, output, [], now, now, TimeSpan.Zero, capabilities)).Value;
        return new ToolInvocationResult(completed, replay, output, [], capabilities);
    }

    private sealed class StubCatalog(ToolDescriptor descriptor) : IToolCatalog
    {
        public ValueTask<DomainResult<IReadOnlyList<ToolSummary>>> SearchAsync(
            ToolSearchRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<DomainResult<ToolDescriptor>> DescribeAsync(
            string toolId,
            string version,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(DomainResult.Success(descriptor));
        }
    }

    private sealed class StubInvocationService(ToolInvocationResult result) : IToolInvocationService
    {
        public ToolInvocationResult Result { get; set; } = result;

        public ToolInvocationRequest? LastRequest { get; private set; }

        public Task<DomainResult<ToolInvocationResult>> InvokeAsync(
            ToolInvocationRequest request,
            IProcessOutputObserver? observer,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastRequest = request;
            return Task.FromResult(DomainResult.Success(Result));
        }
    }

    private sealed class MarkerRedactor : ISensitiveDataRedactor
    {
        public RedactionResult Redact(object? value)
        {
            if (value is string text && text.Contains("redactor-boundary", StringComparison.Ordinal))
            {
                throw new ArgumentException("The fixture redactor rejected the payload bound.", nameof(value));
            }

            return value is string candidate && candidate.Contains("secret-marker", StringComparison.Ordinal)
                ? new RedactionResult(RedactedData.Empty, 1)
                : new RedactionResult(RedactedData.Empty, 0);
        }
    }
}
