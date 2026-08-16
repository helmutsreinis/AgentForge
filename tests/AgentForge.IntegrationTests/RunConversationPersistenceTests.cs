using System.Text;
using AgentForge.Abstractions.Agents;
using AgentForge.Abstractions.Auditing;
using AgentForge.Abstractions.Installations;
using AgentForge.Abstractions.Models;
using AgentForge.Abstractions.Persistence;
using AgentForge.Abstractions.Providers;
using AgentForge.Abstractions.Runtime;
using AgentForge.Abstractions.Security;
using AgentForge.Abstractions.Setup;
using AgentForge.Audit;
using AgentForge.Domain.Agents;
using AgentForge.Domain.Models;
using AgentForge.Domain.Orchestration;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Providers;
using AgentForge.Domain.Runtime;
using AgentForge.Domain.Security;
using AgentForge.Domain.Setup;
using AgentForge.Persistence;
using AgentForge.Runtime;
using AgentForge.Security;
using AgentForge.Setup;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AgentForge.IntegrationTests;

public sealed class RunConversationPersistenceTests : IDisposable
{
    private const string EvidenceHash =
        "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), $"agentforge-conversation-{Guid.NewGuid():N}");

    [Fact]
    public async Task Conversation_redacts_hashes_restarts_and_resumes_without_losing_prior_turns()
    {
        await using var services = BuildServices();
        await InitializeAsync(services);
        var installationId = new InstallationId(Guid.NewGuid());
        var agentId = new AgentIdentityId(Guid.NewGuid());
        var providerId = ProviderProfileId.New();
        await SeedAuthorityAsync(services, installationId, agentId, providerId);

        var conversationId = new RunConversationId(Guid.NewGuid());
        RunConversationSnapshot firstCompleted;
        var secret = "sk-" + new string('x', 30);
        await using (var first = services.CreateAsyncScope())
        {
            var service = first.ServiceProvider.GetRequiredService<IRunConversationService>();
            var created = await service.CreateAsync(new CreateRunConversationRequest(
                conversationId,
                installationId,
                agentId,
                3,
                providerId,
                2,
                "qwen3.8",
                "Restart-safe conversation",
                "You are a bounded local assistant.",
                [],
                Hash('b'),
                Hash('c'),
                Hash('d'),
                new RunConversationTurnId(Guid.NewGuid()),
                new OrchestrationTaskId(Guid.NewGuid()),
                secret,
                "balanced",
                2_048,
                120,
                new ActorId("operator"),
                "conversation-idempotency",
                "turn-one-idempotency",
                new CorrelationId("conversation-test")), CancellationToken.None);
            Assert.True(created.IsSuccess, created.Failure?.Message);
            Assert.Equal(1, created.Value.PersistenceRedactionCount);
            var replay = await service.CreateAsync(new CreateRunConversationRequest(
                conversationId, installationId, agentId, 3, providerId, 2, "qwen3.8",
                "Restart-safe conversation", "You are a bounded local assistant.", [],
                Hash('b'), Hash('c'), Hash('d'), created.Value.Turn.Id, created.Value.Turn.TaskId,
                secret, "balanced", 2_048, 120, new ActorId("operator"),
                "conversation-idempotency", "turn-one-idempotency",
                new CorrelationId("conversation-test")), CancellationToken.None);
            Assert.True(replay.IsSuccess);
            Assert.True(replay.Value.WasReplay);

            var started = await service.StartTurnAsync(
                conversationId, created.Value.Snapshot.Version, created.Value.Turn.Id, CancellationToken.None);
            Assert.True(started.IsSuccess, started.Failure?.Message);
            var completed = await service.CompleteTurnAsync(
                conversationId,
                started.Value.Snapshot.Version,
                started.Value.Turn.Id,
                new LocalModelInteractionResult(
                    new ModelRequestId(started.Value.Turn.TaskId.Value),
                    "First durable answer.",
                    new ModelUsage(10, 4, 0, null, null),
                    ModelFinishReason.Stop,
                    0,
                    5,
                    EvidenceHash),
                CancellationToken.None);
            Assert.True(completed.IsSuccess, completed.Failure?.Message);
            firstCompleted = completed.Value.Snapshot;
        }

        RunConversationSnapshot interrupted;
        await using (var restarted = services.CreateAsyncScope())
        {
            var service = restarted.ServiceProvider.GetRequiredService<IRunConversationService>();
            var details = await service.GetDetailsAsync(conversationId, CancellationToken.None);
            Assert.True(details.IsSuccess, details.Failure?.Message);
            Assert.Equal("[REDACTED]", details.Value.Turns[0].Prompt);
            Assert.Equal("First durable answer.", details.Value.Turns[0].Response);
            Assert.Equal(firstCompleted.SnapshotHash, details.Value.Snapshot.SnapshotHash);

            var second = await service.AddTurnAsync(new AddRunConversationTurnRequest(
                conversationId,
                details.Value.Snapshot.Version,
                new RunConversationTurnId(Guid.NewGuid()),
                new OrchestrationTaskId(Guid.NewGuid()),
                "Use the prior answer and add one verification step.",
                "detailed",
                8_192,
                120,
                "turn-two-idempotency"), CancellationToken.None);
            Assert.True(second.IsSuccess, second.Failure?.Message);
            var started = await service.StartTurnAsync(
                conversationId, second.Value.Snapshot.Version, second.Value.Turn.Id, CancellationToken.None);
            Assert.True(started.IsSuccess, started.Failure?.Message);
            var failed = await service.FailTurnAsync(
                conversationId,
                started.Value.Snapshot.Version,
                started.Value.Turn.Id,
                FailureCode.RecoverableExternalFailure,
                true,
                Hash('e'),
                CancellationToken.None);
            Assert.True(failed.IsSuccess, failed.Failure?.Message);
            Assert.Equal(RunConversationState.NeedsResume, failed.Value.Snapshot.State);
            interrupted = failed.Value.Snapshot;
        }

        await using (var resumed = services.CreateAsyncScope())
        {
            var service = resumed.ServiceProvider.GetRequiredService<IRunConversationService>();
            var started = await service.StartTurnAsync(
                conversationId,
                interrupted.Version,
                interrupted.Turns[^1].Id,
                CancellationToken.None);
            Assert.True(started.IsSuccess, started.Failure?.Message);
            var completed = await service.CompleteTurnAsync(
                conversationId,
                started.Value.Snapshot.Version,
                started.Value.Turn.Id,
                new LocalModelInteractionResult(
                    new ModelRequestId(started.Value.Turn.TaskId.Value),
                    "Second answer after resume.",
                    new ModelUsage(20, 6, 0, null, null),
                    ModelFinishReason.Stop,
                    0,
                    7,
                    Hash('f')),
                CancellationToken.None);
            Assert.True(completed.IsSuccess, completed.Failure?.Message);
            var details = await service.GetDetailsAsync(conversationId, CancellationToken.None);
            Assert.Equal(2, details.Value.Turns.Count);
            Assert.Equal("Second answer after resume.", details.Value.Turns[1].Response);
            Assert.True((await resumed.ServiceProvider.GetRequiredService<IAuditIntegrityVerifier>()
                .VerifyAsync(CancellationToken.None)).IsValid);
        }

        var database = await File.ReadAllBytesAsync(Path.Combine(_directory, "conversation.db"));
        Assert.DoesNotContain(secret, Encoding.UTF8.GetString(database), StringComparison.Ordinal);
        foreach (var file in Directory.EnumerateFiles(Path.Combine(_directory, "artifacts"), "*", SearchOption.AllDirectories))
        {
            Assert.DoesNotContain(secret, await File.ReadAllTextAsync(file), StringComparison.Ordinal);
        }
    }

    private ServiceProvider BuildServices()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AgentForge:Installation:DataDirectory"] = _directory,
            ["AgentForge:Persistence:DatabaseFileName"] = "conversation.db",
            ["AgentForge:Persistence:EnableConnectionPooling"] = "false",
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAgentForgeSetup(configuration);
        services.AddAgentForgePersistence(configuration);
        services.AddAgentForgeSecurity(configuration);
        services.AddAgentForgeAudit();
        services.AddAgentForgeRuntime();
        return services.BuildServiceProvider(validateScopes: true);
    }

    private static async Task InitializeAsync(ServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>()
            .InitializeAsync(CancellationToken.None);
    }

    private static async Task SeedAuthorityAsync(
        ServiceProvider services,
        InstallationId installationId,
        AgentIdentityId agentId,
        ProviderProfileId providerId)
    {
        await using var scope = services.CreateAsyncScope();
        var setup = await scope.ServiceProvider.GetRequiredService<ISetupApplicationService>().BeginAsync(
            new BeginSetupRequest(installationId, new ActorId("operator"), new CorrelationId("setup")),
            CancellationToken.None);
        Assert.True(setup.IsSuccess, setup.Failure?.Message);
        var now = DateTimeOffset.UtcNow;
        await scope.ServiceProvider.GetRequiredService<IProviderProfileRepository>().AddAsync(new ProviderProfile(
            providerId,
            installationId,
            "local",
            "openai-compatible",
            new Uri("http://127.0.0.1:8000/v1"),
            "qwen3.8",
            SecretReference.NoCredential,
            new ProviderCapabilitySummary(true, true, false, false, "fixture"),
            2,
            now,
            now,
            new ActorId("operator"),
            new CorrelationId("provider")), CancellationToken.None);
        await scope.ServiceProvider.GetRequiredService<IAgentIdentityRepository>().AddAsync(new AgentIdentity(
            agentId,
            installationId,
            "local-agent",
            null,
            null,
            "en",
            "Europe/Kiev",
            "balanced",
            null,
            new AgentModelPolicy(providerId, ModelDataLocality.LocalOnly, false),
            new AgentMemoryPolicy(AgentMemoryScope.Agent, 30),
            new AgentCapabilityPolicy(NetworkPosture.Denied, [], []),
            new AgentBudget(4, 0, 16_000, 16_384, 120),
            new ChildAgentLimits(0, 0, 0, 0),
            new AgentLearningPolicy(LearningMode.Propose, MutableSkillScope.ProposalWorkspaceOnly),
            3,
            now,
            now,
            new ActorId("operator"),
            new CorrelationId("agent")), CancellationToken.None);
        Assert.True((await scope.ServiceProvider.GetRequiredService<IUnitOfWork>()
            .CommitAsync(CancellationToken.None)).Succeeded);
    }

    private static string Hash(char character) => $"sha256:{new string(character, 64)}";

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
