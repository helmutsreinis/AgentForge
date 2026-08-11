using AgentForge.Abstractions.Agents;
using AgentForge.Abstractions.Auditing;
using AgentForge.Abstractions.Installations;
using AgentForge.Abstractions.Memory;
using AgentForge.Abstractions.Persistence;
using AgentForge.Abstractions.Providers;
using AgentForge.Domain.Agents;
using AgentForge.Domain.Installations;
using AgentForge.Domain.Memory;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Providers;
using AgentForge.Memory;
using AgentForge.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace AgentForge.IntegrationTests;

public sealed partial class PersistenceFoundationTests
{
    [Fact]
    public async Task Durable_memory_is_redacted_scoped_searchable_attributable_and_removable()
    {
        var database = "memory.db";
        await using var services = BuildServices(
            _directory,
            database,
            collection => collection.AddAgentForgeMemory());
        await using (var initialize = services.CreateAsyncScope())
        {
            await initialize.ServiceProvider.GetRequiredService<IDatabaseInitializer>()
                .InitializeAsync(CancellationToken.None);
        }

        var installationId = new InstallationId(Guid.Parse("3a7a96c2-95d6-45bb-b61c-3e73e14f73cd"));
        var providerId = new ProviderProfileId(Guid.Parse("72c5cd62-3f03-4bc9-ab0f-c84043827852"));
        var agentId = new AgentIdentityId(Guid.Parse("0669aedb-0200-40a5-b90e-4cb9de49fdf3"));
        await using (var seed = services.CreateAsyncScope())
        {
            await seed.ServiceProvider.GetRequiredService<IInstallationRepository>().AddAsync(
                InstallationSnapshot.CreateUninitialized(
                    installationId, Now, new ActorId("memory-operator"), new CorrelationId("memory-seed")),
                CancellationToken.None);
            await seed.ServiceProvider.GetRequiredService<IProviderProfileRepository>().AddAsync(
                CreateProviderProfile(installationId, providerId, "memory"), CancellationToken.None);
            var candidate = CreateAgentCandidate(providerId);
            await seed.ServiceProvider.GetRequiredService<IAgentIdentityRepository>().AddAsync(new AgentIdentity(
                agentId, installationId, candidate.Name, candidate.Expertise, candidate.Mission,
                candidate.PreferredLanguage, candidate.TimeZone, candidate.ResponseStyle,
                candidate.DefaultWorkspace, candidate.ModelPolicy, candidate.MemoryPolicy,
                candidate.CapabilityPolicy, candidate.Budget, candidate.ChildLimits,
                candidate.LearningPolicy, 0, Now, Now, new ActorId("memory-operator"),
                new CorrelationId("memory-seed")), CancellationToken.None);
            Assert.True((await seed.ServiceProvider.GetRequiredService<IUnitOfWork>()
                .CommitAsync(CancellationToken.None)).Succeeded);
        }

        var request = new CreateMemoryRequest(
            new MemoryEntryId(Guid.Parse("64d48d36-97d9-441a-a172-5279b3733a33")),
            installationId,
            agentId,
            "task:research",
            MemoryKind.Semantic,
            "password=raw-memory-secret",
            new MemorySource(
                MemorySourceKind.SearchCitation,
                "cite-123",
                $"sha256:{new string('a', 64)}",
                new Uri("https://example.test/cited")),
            Now.AddDays(30),
            new ActorId("memory-operator"),
            new CorrelationId("memory-create"),
            null,
            "memory-create-001");

        await using (var create = services.CreateAsyncScope())
        {
            var result = await create.ServiceProvider.GetRequiredService<IMemoryService>()
                .CreateAsync(request, CancellationToken.None);
            Assert.True(result.IsSuccess, result.Failure?.Message);
            Assert.Equal("[REDACTED]", result.Value.Content);
            Assert.Equal(1, result.Value.RedactionCount);
        }

        await using (var replay = services.CreateAsyncScope())
        {
            var service = replay.ServiceProvider.GetRequiredService<IMemoryService>();
            var result = await service.CreateAsync(request, CancellationToken.None);
            Assert.True(result.IsSuccess, result.Failure?.Message);
            var found = await service.SearchAsync(new MemoryQuery(
                installationId, agentId, "task:research", "REDACTED", [MemoryKind.Semantic], 10, Now),
                CancellationToken.None);
            Assert.True(found.IsSuccess);
            Assert.Single(found.Value);
            var wildcard = await service.SearchAsync(new MemoryQuery(
                installationId, agentId, "task:research", "%", [MemoryKind.Semantic], 10, Now),
                CancellationToken.None);
            Assert.True(wildcard.IsSuccess);
            Assert.Empty(wildcard.Value);
        }

        await using (var remove = services.CreateAsyncScope())
        {
            var deleted = await remove.ServiceProvider.GetRequiredService<IMemoryService>().DeleteAsync(
                new DeleteMemoryRequest(
                    request.Id, installationId, agentId, "task:research", new ActorId("memory-operator"),
                    new CorrelationId("memory-delete"), null), CancellationToken.None);
            Assert.True(deleted.IsSuccess && deleted.Value);
        }

        await using (var verify = services.CreateAsyncScope())
        {
            var found = await verify.ServiceProvider.GetRequiredService<IMemoryService>().SearchAsync(
                new MemoryQuery(
                    installationId, agentId, "task:research", "REDACTED", [MemoryKind.Semantic], 10, Now),
                CancellationToken.None);
            Assert.True(found.IsSuccess);
            Assert.Empty(found.Value);
            var integrity = await verify.ServiceProvider.GetRequiredService<IAuditIntegrityVerifier>()
                .VerifyAsync(CancellationToken.None);
            Assert.True(integrity.IsValid);
        }

        var databaseBytes = await File.ReadAllBytesAsync(Path.Combine(_directory, database));
        Assert.DoesNotContain("raw-memory-secret", System.Text.Encoding.UTF8.GetString(databaseBytes), StringComparison.Ordinal);
    }
}
