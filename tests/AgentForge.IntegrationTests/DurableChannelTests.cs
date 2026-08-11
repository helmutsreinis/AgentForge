using System.Collections.Immutable;
using System.Text.Json;
using AgentForge.Abstractions.Agents;
using AgentForge.Abstractions.Auditing;
using AgentForge.Abstractions.Channels;
using AgentForge.Abstractions.Installations;
using AgentForge.Abstractions.Persistence;
using AgentForge.Abstractions.Providers;
using AgentForge.Channels;
using AgentForge.Domain.Agents;
using AgentForge.Domain.Channels;
using AgentForge.Domain.Installations;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Providers;
using AgentForge.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace AgentForge.IntegrationTests;

public sealed partial class PersistenceFoundationTests
{
    [Fact]
    public async Task Authenticated_channel_message_is_durable_identity_bound_and_replay_safe()
    {
        var adapter = new DeterministicChannelAdapter(ChannelKind.Telegram, "durable-bot", "webhook-secret");
        await using var services = BuildServices(_directory, "channels.db", collection =>
        {
            collection.AddSingleton<IChannelAdapterCatalog>(new ChannelAdapterCatalog([adapter]));
            collection.AddSingleton<IChannelAttachmentScanner, DurableCleanScanner>();
            collection.AddAgentForgeChannels();
        });
        await using (var initialize = services.CreateAsyncScope())
        {
            await initialize.ServiceProvider.GetRequiredService<IDatabaseInitializer>()
                .InitializeAsync(CancellationToken.None);
        }

        var installationId = new InstallationId(Guid.Parse("bc07e69a-b7d8-44d5-8d70-da22688b848d"));
        var providerId = new ProviderProfileId(Guid.Parse("984b74c4-798c-4682-aa96-590674c4c07b"));
        var agentId = new AgentIdentityId(Guid.Parse("b6c1c62f-84ce-4830-9457-25418d39972b"));
        await using (var seed = services.CreateAsyncScope())
        {
            await seed.ServiceProvider.GetRequiredService<IInstallationRepository>().AddAsync(
                InstallationSnapshot.CreateUninitialized(
                    installationId, Now, new ActorId("channel-admin"), new CorrelationId("channel-seed")),
                CancellationToken.None);
            await seed.ServiceProvider.GetRequiredService<IProviderProfileRepository>().AddAsync(
                CreateProviderProfile(installationId, providerId, "channel"), CancellationToken.None);
            var candidate = CreateAgentCandidate(providerId);
            await seed.ServiceProvider.GetRequiredService<IAgentIdentityRepository>().AddAsync(new AgentIdentity(
                agentId, installationId, candidate.Name, candidate.Expertise, candidate.Mission,
                candidate.PreferredLanguage, candidate.TimeZone, candidate.ResponseStyle,
                candidate.DefaultWorkspace, candidate.ModelPolicy, candidate.MemoryPolicy,
                candidate.CapabilityPolicy, candidate.Budget, candidate.ChildLimits,
                candidate.LearningPolicy, 0, Now, Now, new ActorId("channel-admin"),
                new CorrelationId("channel-seed")), CancellationToken.None);
            await seed.ServiceProvider.GetRequiredService<IChannelIdentityBindingStore>().AddAsync(
                new ChannelIdentityBinding(
                    installationId, agentId, new ActorId("telegram:user-42"), ChannelKind.Telegram,
                    "durable-bot", "user-42", $"sha256:{new string('a', 64)}"), CancellationToken.None);
            Assert.True((await seed.ServiceProvider.GetRequiredService<IUnitOfWork>()
                .CommitAsync(CancellationToken.None)).Succeeded);
        }

        var body = JsonSerializer.SerializeToUtf8Bytes(new
        {
            messageId = "telegram-message-7",
            senderId = "user-42",
            recipientId = "durable-bot",
            text = "authenticated task",
            timestamp = Now.ToUnixTimeSeconds(),
            attachments = Array.Empty<object>(),
        });
        var request = new ChannelWebhookRequest(
            ChannelKind.Telegram, "durable-bot",
            ImmutableDictionary<string, string>.Empty.Add("X-AgentForge-Webhook-Secret", "webhook-secret"),
            body, Now, new CorrelationId("channel-receive"));
        NormalizedInboundChannelMessage received;
        await using (var receive = services.CreateAsyncScope())
        {
            var result = await receive.ServiceProvider.GetRequiredService<IChannelService>()
                .ReceiveAsync(request, CancellationToken.None);
            Assert.True(result.IsSuccess, result.Failure?.Message);
            received = result.Value;
        }

        await using (var replay = services.CreateAsyncScope())
        {
            var result = await replay.ServiceProvider.GetRequiredService<IChannelService>()
                .ReceiveAsync(request, CancellationToken.None);
            Assert.True(result.IsSuccess, result.Failure?.Message);
            Assert.Equal(received, result.Value);
            var audit = await replay.ServiceProvider.GetRequiredService<IAuditIntegrityVerifier>()
                .VerifyAsync(CancellationToken.None);
            Assert.True(audit.IsValid);
        }
    }

    private sealed class DurableCleanScanner : IChannelAttachmentScanner
    {
        public ValueTask<AttachmentScanStatus> ScanAsync(ChannelAttachment attachment, CancellationToken cancellationToken) =>
            ValueTask.FromResult(AttachmentScanStatus.Clean);
    }
}
