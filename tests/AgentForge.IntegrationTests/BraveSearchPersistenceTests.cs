using AgentForge.Abstractions.Installations;
using AgentForge.Abstractions.Persistence;
using AgentForge.Abstractions.Search;
using AgentForge.Domain.Installations;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Search;
using AgentForge.Domain.Security;
using AgentForge.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace AgentForge.IntegrationTests;

public sealed partial class PersistenceFoundationTests
{
    [Fact]
    public async Task Brave_search_profile_persists_opaque_reference_and_versions_rotation_policy()
    {
        const string database = "brave-search.db";
        await using var services = BuildServices(_directory, database);
        await using (var initialize = services.CreateAsyncScope())
        {
            await initialize.ServiceProvider.GetRequiredService<IDatabaseInitializer>()
                .InitializeAsync(CancellationToken.None);
        }

        var installationId = new InstallationId(Guid.NewGuid());
        var initial = new SearchProviderProfile(
            installationId,
            "brave",
            SearchProviderKind.Brave,
            new Uri("https://api.search.brave.com/res/v1/web/search"),
            new SecretReference("test-store", "opaque-key-one"),
            true,
            SearchSafeSearch.Moderate,
            "UA",
            "en",
            0,
            Now,
            Now,
            new ActorId("operator"),
            new CorrelationId("brave-create"));
        await using (var seed = services.CreateAsyncScope())
        {
            await seed.ServiceProvider.GetRequiredService<IInstallationRepository>().AddAsync(
                InstallationSnapshot.CreateUninitialized(
                    installationId, Now, new ActorId("operator"), new CorrelationId("seed")),
                CancellationToken.None);
            await seed.ServiceProvider.GetRequiredService<ISearchProviderProfileRepository>()
                .AddAsync(initial, CancellationToken.None);
            Assert.True((await seed.ServiceProvider.GetRequiredService<IUnitOfWork>()
                .CommitAsync(CancellationToken.None)).Succeeded);
        }

        await using (var update = services.CreateAsyncScope())
        {
            var repository = update.ServiceProvider.GetRequiredService<ISearchProviderProfileRepository>();
            var current = await repository.FindAsync(installationId, "brave", CancellationToken.None);
            Assert.NotNull(current);
            var rotated = current with
            {
                CredentialReference = new SecretReference("test-store", "opaque-key-two"),
                SafeSearch = SearchSafeSearch.Strict,
                Version = 1,
                UpdatedAtUtc = Now.AddMinutes(1),
                CorrelationId = new CorrelationId("brave-rotate"),
            };
            await repository.UpdateAsync(rotated, 0, CancellationToken.None);
            Assert.True((await update.ServiceProvider.GetRequiredService<IUnitOfWork>()
                .CommitAsync(CancellationToken.None)).Succeeded);
        }

        await using (var verify = services.CreateAsyncScope())
        {
            var stored = await verify.ServiceProvider.GetRequiredService<ISearchProviderProfileRepository>()
                .FindAsync(installationId, "brave", CancellationToken.None);
            Assert.NotNull(stored);
            Assert.Equal(1, stored.Version);
            Assert.Equal(SearchSafeSearch.Strict, stored.SafeSearch);
            Assert.Equal("opaque-key-two", stored.CredentialReference.Key);
            Assert.NotEqual(initial.EvidenceHash, stored.EvidenceHash);
            Assert.Single(await verify.ServiceProvider.GetRequiredService<ISearchProviderProfileRepository>()
                .ListAsync(installationId, CancellationToken.None));
        }
    }
}
