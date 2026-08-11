using System.Collections.Immutable;
using System.Text.Json;
using AgentForge.Abstractions.Agents;
using AgentForge.Abstractions.Auditing;
using AgentForge.Abstractions.Channels;
using AgentForge.Abstractions.Installations;
using AgentForge.Abstractions.Persistence;
using AgentForge.Abstractions.Security;
using AgentForge.Abstractions.Time;
using AgentForge.Channels;
using AgentForge.Domain.Agents;
using AgentForge.Domain.Auditing;
using AgentForge.Domain.Channels;
using AgentForge.Domain.Installations;
using AgentForge.Domain.Persistence;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Providers;
using AgentForge.Domain.Security;
using AgentForge.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AgentForge.UnitTests;

public sealed class ChannelServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 12, 12, 0, 0, TimeSpan.Zero);
    private static readonly InstallationId InstallationId = new(Guid.Parse("53ef6546-e609-4b48-90d0-da53cc8c52bd"));
    private static readonly AgentIdentityId AgentId = new(Guid.Parse("e11c1906-d2af-4bd2-a738-06718657df8d"));

    [Fact]
    public async Task Authenticated_inbound_message_binds_identity_scans_and_deduplicates()
    {
        var adapter = new DeterministicChannelAdapter(ChannelKind.Telegram, "bot-1", "webhook-secret");
        var repository = new FakeChannelRepository();
        await using var provider = BuildProvider(adapter, repository);
        await using var scope = provider.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IChannelService>();
        var request = Webhook("message-1", "hello");

        var first = await service.ReceiveAsync(request, CancellationToken.None);
        var replay = await service.ReceiveAsync(request, CancellationToken.None);

        Assert.True(first.IsSuccess, first.Failure?.Message);
        Assert.Equal(first.Value, replay.Value);
        Assert.Single(repository.Inbound);
        Assert.Equal("sender-1", first.Value.ExternalSenderId);
        Assert.StartsWith(Now.UtcTicks.ToString("D19", System.Globalization.CultureInfo.InvariantCulture), first.Value.OrderKey, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invalid_authentication_and_conflicting_replay_fail_closed()
    {
        var adapter = new DeterministicChannelAdapter(ChannelKind.Telegram, "bot-1", "webhook-secret");
        var repository = new FakeChannelRepository();
        await using var provider = BuildProvider(adapter, repository);
        await using var scope = provider.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IChannelService>();
        var denied = await service.ReceiveAsync(Webhook("message-1", "hello") with
        {
            Headers = ImmutableDictionary<string, string>.Empty.Add("X-AgentForge-Webhook-Secret", "wrong"),
        }, CancellationToken.None);
        Assert.Equal(FailureCode.PolicyDenied, denied.Failure?.Code);

        Assert.True((await service.ReceiveAsync(Webhook("message-1", "hello"), CancellationToken.None)).IsSuccess);
        var conflict = await service.ReceiveAsync(Webhook("message-1", "changed"), CancellationToken.None);
        Assert.Equal(FailureCode.ConcurrencyConflict, conflict.Failure?.Code);
    }

    [Fact]
    public async Task Outbound_send_requires_exact_approval_then_retries_definite_failure_once()
    {
        var adapter = new DeterministicChannelAdapter(ChannelKind.Telegram, "bot-1", "webhook-secret", [
            new ChannelAdapterSendResult(false, true, false, null, Hash('b')),
            new ChannelAdapterSendResult(true, false, false, "provider-42", Hash('c'))]);
        var repository = new FakeChannelRepository();
        var approvals = new FakeApprovalRepository();
        await using var provider = BuildProvider(adapter, repository, approvals);
        var request = SendRequest();

        await AddExactApprovalAsync(provider, approvals, request);
        await using var scope = provider.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IChannelService>();
        var first = await service.SendAsync(request, CancellationToken.None);
        var second = await service.SendAsync(request, CancellationToken.None);
        var replay = await service.SendAsync(request, CancellationToken.None);

        Assert.Equal(ChannelDeliveryState.RetryPending, first.Value.State);
        Assert.Equal(ChannelDeliveryState.Sent, second.Value.State);
        Assert.Equal("provider-42", second.Value.ProviderMessageId);
        Assert.Equal(second.Value, replay.Value);
        Assert.Equal(2, adapter.SendCount);
        Assert.Equal(CapabilityApprovalState.Consumed, approvals.Approval?.State);
    }

    [Fact]
    public async Task Missing_or_recipient_substituted_approval_never_sends()
    {
        var adapter = new DeterministicChannelAdapter(ChannelKind.WhatsApp, "business-1", "webhook-secret");
        var approvals = new FakeApprovalRepository();
        await using var provider = BuildProvider(adapter, new FakeChannelRepository(), approvals);
        var approvedRequest = SendRequest() with { Channel = ChannelKind.WhatsApp, AccountId = "business-1" };
        await AddExactApprovalAsync(provider, approvals, approvedRequest);
        await using var scope = provider.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<IChannelService>().SendAsync(
            approvedRequest with { RecipientId = "different-recipient" }, CancellationToken.None);

        Assert.Equal(FailureCode.ApprovalRequired, result.Failure?.Code);
        Assert.Equal(0, adapter.SendCount);
    }

    [Fact]
    public async Task Uncertain_delivery_dead_letters_and_is_never_replayed()
    {
        var adapter = new DeterministicChannelAdapter(ChannelKind.Telegram, "bot-1", "webhook-secret", [
            new ChannelAdapterSendResult(false, true, true, null, Hash('d'))]);
        var approvals = new FakeApprovalRepository();
        await using var provider = BuildProvider(adapter, new FakeChannelRepository(), approvals);
        var request = SendRequest();
        await AddExactApprovalAsync(provider, approvals, request);
        await using var scope = provider.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IChannelService>();

        var first = await service.SendAsync(request, CancellationToken.None);
        var replay = await service.SendAsync(request, CancellationToken.None);

        Assert.Equal(ChannelDeliveryState.DeadLetter, first.Value.State);
        Assert.Equal(first.Value, replay.Value);
        Assert.Equal(1, adapter.SendCount);
    }

    [Fact]
    public async Task Quiet_hours_and_rate_limits_precede_adapter_invocation()
    {
        var adapter = new DeterministicChannelAdapter(ChannelKind.Telegram, "bot-1", "webhook-secret");
        var repository = new FakeChannelRepository { SentCount = 1 };
        await using var provider = BuildProvider(adapter, repository);
        await using var scope = provider.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IChannelService>();
        var rate = await service.SendAsync(SendRequest() with
        {
            Policy = new ChannelDeliveryPolicy("UTC", new TimeOnly(1, 0), new TimeOnly(2, 0), 1, 3),
        }, CancellationToken.None);
        var quiet = await service.SendAsync(SendRequest() with
        {
            Id = new ChannelDeliveryId(Guid.NewGuid()),
            IdempotencyKey = "send-quiet",
            Policy = new ChannelDeliveryPolicy("UTC", new TimeOnly(11, 0), new TimeOnly(13, 0), 10, 3),
        }, CancellationToken.None);

        Assert.Equal(FailureCode.BudgetExceeded, rate.Failure?.Code);
        Assert.Equal(FailureCode.PolicyDenied, quiet.Failure?.Code);
        Assert.Equal(0, adapter.SendCount);
    }

    private static async Task AddExactApprovalAsync(
        ServiceProvider provider,
        FakeApprovalRepository approvals,
        ChannelSendRequest request)
    {
        await using var scope = provider.CreateAsyncScope();
        var contentHash = ChannelEvidence.ContentHash(request.Text, request.Attachments);
        var context = scope.ServiceProvider.GetRequiredService<IAuthorizationContextFactory>().Create(
            new CapabilityInvocationRequest(
                request.InstallationId, request.ExpectedInstallationVersion, request.AgentId,
                request.AgentVersion, request.ActorId, "channel:send", CapabilityRiskClass.ExternalMutation,
                null, null, null,
                JsonSerializer.Serialize(new { request.Channel, request.AccountId, request.RecipientId, contentHash }),
                AuthorizationTargetKind.Recipient, request.RecipientId, null,
                request.CorrelationId, request.CausationId));
        Assert.True(context.IsSuccess);
        var created = CapabilityApprovalStateMachine.Create(
            new CapabilityApprovalId(Guid.NewGuid()), context.Value, CapabilityApprovalDisposition.Grant,
            Now.AddMinutes(-1), Now.AddHours(1), new ActorId("administrator"),
            new CorrelationId("approval"), Hash('e'), "approval-idempotency");
        Assert.True(created.IsSuccess);
        approvals.Approval = created.Value;
    }

    private static ServiceProvider BuildProvider(
        IChannelAdapter adapter,
        FakeChannelRepository repository,
        FakeApprovalRepository? approvals = null)
    {
        var services = new ServiceCollection();
        services.AddAgentForgeSecurity(new ConfigurationBuilder().Build());
        services.AddSingleton<IChannelAdapterCatalog>(new ChannelAdapterCatalog([adapter]));
        services.AddSingleton<IChannelRepository>(repository);
        services.AddSingleton<IChannelIdentityResolver>(new FakeIdentityResolver());
        services.AddSingleton<IChannelAttachmentScanner>(new CleanScanner());
        services.AddSingleton<IInstallationRepository>(new FakeInstallationRepository());
        services.AddSingleton<IAgentIdentityRepository>(new FakeAgentRepository());
        services.AddSingleton<ICapabilityApprovalRepository>(approvals ?? new FakeApprovalRepository());
        services.AddSingleton<IAuditRecorder>(new FakeAuditRecorder());
        services.AddSingleton<IUnitOfWork>(new FakeUnitOfWork());
        services.AddSingleton<IClock>(new FakeClock());
        services.AddSingleton<IIdentifierGenerator>(new FakeClock());
        services.AddAgentForgeChannels();
        return services.BuildServiceProvider(validateScopes: true);
    }

    private static ChannelWebhookRequest Webhook(string messageId, string text)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(new
        {
            messageId,
            senderId = "sender-1",
            recipientId = "bot-1",
            text,
            timestamp = Now.ToUnixTimeSeconds(),
            attachments = Array.Empty<object>(),
        });
        return new ChannelWebhookRequest(
            ChannelKind.Telegram, "bot-1",
            ImmutableDictionary<string, string>.Empty.Add("X-AgentForge-Webhook-Secret", "webhook-secret"),
            body, Now, new CorrelationId("webhook-correlation"));
    }

    private static ChannelSendRequest SendRequest() => new(
        new ChannelDeliveryId(Guid.Parse("23c0f522-4358-44b3-94ea-b5e43d18b60e")), InstallationId, 1,
        AgentId, 0, new ActorId("operator"), ChannelKind.Telegram, "bot-1", "recipient-1", "approved text",
        [], new ChannelDeliveryPolicy("UTC", new TimeOnly(1, 0), new TimeOnly(2, 0), 10, 3),
        "send-idempotency", new CorrelationId("send-correlation"), null);

    private static string Hash(char value) => $"sha256:{new string(value, 64)}";

    private sealed class FakeClock : IClock, IIdentifierGenerator
    {
        public DateTimeOffset UtcNow => Now;
        public Guid NewGuid() => Guid.NewGuid();
    }

    private sealed class CleanScanner : IChannelAttachmentScanner
    {
        public ValueTask<AttachmentScanStatus> ScanAsync(ChannelAttachment attachment, CancellationToken cancellationToken) =>
            ValueTask.FromResult(AttachmentScanStatus.Clean);
    }

    private sealed class FakeIdentityResolver : IChannelIdentityResolver
    {
        public ValueTask<ChannelIdentityBinding?> ResolveAsync(
            ChannelKind channel, string accountId, string externalSenderId, CancellationToken cancellationToken) =>
            ValueTask.FromResult<ChannelIdentityBinding?>(new(
                InstallationId, AgentId, new ActorId("channel:sender-1"), channel,
                accountId, externalSenderId, Hash('a')));
    }

    private sealed class FakeInstallationRepository : IInstallationRepository
    {
        public ValueTask<InstallationSnapshot> ReadAsync(CancellationToken cancellationToken) => ValueTask.FromResult(
            InstallationSnapshot.CreateUninitialized(InstallationId, Now, new ActorId("operator"), new CorrelationId("seed")) with
            { State = InstallationState.Ready, Version = 1 });
        public ValueTask AddAsync(InstallationSnapshot snapshot, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask UpdateAsync(InstallationSnapshot snapshot, long expectedVersion, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private sealed class FakeAgentRepository : IAgentIdentityRepository
    {
        private static readonly AgentIdentity Agent = new(
            AgentId, InstallationId, "channel-agent", null, null, "en", "UTC", "concise", null,
            new AgentModelPolicy(new ProviderProfileId(Guid.NewGuid()), ModelDataLocality.LocalOnly, false),
            new AgentMemoryPolicy(AgentMemoryScope.Task, 30),
            new AgentCapabilityPolicy(NetworkPosture.Denied, ["channel:send"], []),
            new AgentBudget(10, 10, 1000, 1000, 60), new ChildAgentLimits(1, 1, 1, 1000),
            new AgentLearningPolicy(LearningMode.Off, MutableSkillScope.None), 0, Now, Now,
            new ActorId("operator"), new CorrelationId("seed"));
        public ValueTask AddAsync(AgentIdentity agent, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask UpdateAsync(AgentIdentity agent, long expectedVersion, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask<AgentIdentity?> FindByNameAsync(InstallationId installationId, string name, CancellationToken cancellationToken) => ValueTask.FromResult<AgentIdentity?>(Agent);
        public ValueTask<AgentIdentity?> FindByIdAsync(AgentIdentityId agentId, CancellationToken cancellationToken) => ValueTask.FromResult<AgentIdentity?>(agentId == AgentId ? Agent : null);
        public Task<IReadOnlyList<AgentIdentity>> ListAsync(InstallationId installationId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<AgentIdentity>>([Agent]);
    }

    private sealed class FakeApprovalRepository : ICapabilityApprovalRepository
    {
        public CapabilityApproval? Approval { get; set; }
        public ValueTask AddAsync(CapabilityApproval approval, CancellationToken cancellationToken) { Approval = approval; return ValueTask.CompletedTask; }
        public ValueTask UpdateAsync(CapabilityApproval approval, long expectedVersion, CancellationToken cancellationToken) { Approval = approval; return ValueTask.CompletedTask; }
        public ValueTask<CapabilityApproval?> FindByIdAsync(CapabilityApprovalId approvalId, CancellationToken cancellationToken) => ValueTask.FromResult(Approval);
        public ValueTask<CapabilityApproval?> FindByIdempotencyKeyAsync(InstallationId installationId, string idempotencyKey, CancellationToken cancellationToken) => ValueTask.FromResult(Approval);
        public ValueTask<CapabilityApproval?> FindLatestAsync(InstallationId installationId, AgentIdentityId agentId, string requestHash, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Approval?.RequestHash == requestHash ? Approval : null);
    }

    private sealed class FakeChannelRepository : IChannelRepository
    {
        public List<NormalizedInboundChannelMessage> Inbound { get; } = [];
        public ChannelDelivery? Delivery { get; private set; }
        public int SentCount { get; set; }
        public ValueTask<NormalizedInboundChannelMessage?> FindInboundAsync(ChannelKind channel, string accountId, string externalMessageId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Inbound.SingleOrDefault(item => item.Channel == channel && item.AccountId == accountId && item.ExternalMessageId == externalMessageId));
        public ValueTask AddInboundAsync(NormalizedInboundChannelMessage message, CancellationToken cancellationToken) { Inbound.Add(message); return ValueTask.CompletedTask; }
        public ValueTask<ChannelDelivery?> FindDeliveryByIdempotencyKeyAsync(InstallationId installationId, string idempotencyKey, CancellationToken cancellationToken) => ValueTask.FromResult(Delivery);
        public ValueTask AddDeliveryAsync(ChannelDelivery delivery, CancellationToken cancellationToken) { Delivery = delivery; return ValueTask.CompletedTask; }
        public ValueTask UpdateDeliveryAsync(ChannelDelivery delivery, long expectedVersion, CancellationToken cancellationToken) { Assert.Equal(expectedVersion, Delivery?.Version); Delivery = delivery; return ValueTask.CompletedTask; }
        public ValueTask<int> CountSentAsync(InstallationId installationId, AgentIdentityId agentId, ChannelKind channel, DateTimeOffset sinceUtc, CancellationToken cancellationToken) => ValueTask.FromResult(SentCount);
    }

    private sealed class FakeUnitOfWork : IUnitOfWork
    {
        public Task<CommitResult> CommitAsync(CancellationToken cancellationToken) => Task.FromResult(CommitResult.Success(1));
    }

    private sealed class FakeAuditRecorder : IAuditRecorder
    {
        public Task<AuditRecordResult> RecordAsync(AuditRecordRequest request, CancellationToken cancellationToken) => Task.FromResult(
            new AuditRecordResult(new AuditEventRecord(
                Guid.NewGuid(), 1, Now, request.InstallationId, request.ActorId, request.CorrelationId,
                request.CausationId, request.OperationType, request.Outcome, RedactedData.Empty,
                RedactedData.Empty, request.ErrorClassification, new string('0', 64), new string('1', 64)), 0, 0));
    }
}
