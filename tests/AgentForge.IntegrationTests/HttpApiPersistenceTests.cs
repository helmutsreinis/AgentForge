using AgentForge.Abstractions.HttpApi;
using AgentForge.Abstractions.Installations;
using AgentForge.Abstractions.Persistence;
using AgentForge.Domain.HttpApi;
using AgentForge.Domain.Installations;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Security;
using AgentForge.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace AgentForge.IntegrationTests;

public sealed partial class PersistenceFoundationTests
{
    [Fact]
    public async Task Generated_skill_api_profile_persists_only_an_opaque_bearer_reference_and_versions_rotation()
    {
        const string database = "http-api-profile.db";
        await using var services = BuildServices(_directory, database);
        await using (var initialize = services.CreateAsyncScope())
        {
            await initialize.ServiceProvider.GetRequiredService<IDatabaseInitializer>()
                .InitializeAsync(CancellationToken.None);
        }

        var installationId = new InstallationId(Guid.NewGuid());
        var initial = new HttpApiProfile(
            installationId,
            new HttpApiProfileId("microsoft-partner-center"),
            "Microsoft Partner Center",
            new Uri("https://api.partnercenter.microsoft.com/v1/"),
            "customers?size=1",
            new Dictionary<string, string>
            {
                ["MS-Contract-Version"] = "v1",
                ["MS-CorrelationId"] = "{correlationId}",
                ["MS-RequestId"] = "{requestId}",
            },
            new SecretReference("test-store", "opaque-token-reference-one"),
            true,
            0,
            Now,
            Now,
            new ActorId("operator"),
            new CorrelationId("http-api-create"));
        await using (var seed = services.CreateAsyncScope())
        {
            await seed.ServiceProvider.GetRequiredService<IInstallationRepository>().AddAsync(
                InstallationSnapshot.CreateUninitialized(
                    installationId, Now, new ActorId("operator"), new CorrelationId("seed")),
                CancellationToken.None);
            await seed.ServiceProvider.GetRequiredService<IHttpApiProfileRepository>()
                .AddAsync(initial, CancellationToken.None);
            Assert.True((await seed.ServiceProvider.GetRequiredService<IUnitOfWork>()
                .CommitAsync(CancellationToken.None)).Succeeded);
        }

        await using (var update = services.CreateAsyncScope())
        {
            var repository = update.ServiceProvider.GetRequiredService<IHttpApiProfileRepository>();
            var current = await repository.FindAsync(
                installationId, initial.Id, CancellationToken.None);
            Assert.NotNull(current);
            var rotated = current with
            {
                CredentialReference = new SecretReference("test-store", "opaque-token-reference-two"),
                Version = 1,
                UpdatedAtUtc = Now.AddMinutes(1),
                CorrelationId = new CorrelationId("http-api-rotate"),
            };
            await repository.UpdateAsync(rotated, 0, CancellationToken.None);
            Assert.True((await update.ServiceProvider.GetRequiredService<IUnitOfWork>()
                .CommitAsync(CancellationToken.None)).Succeeded);
        }

        await using (var verify = services.CreateAsyncScope())
        {
            var repository = verify.ServiceProvider.GetRequiredService<IHttpApiProfileRepository>();
            var stored = await repository.FindAsync(installationId, initial.Id, CancellationToken.None);
            Assert.NotNull(stored);
            Assert.Equal(1, stored.Version);
            Assert.Equal("opaque-token-reference-two", stored.CredentialReference.Key);
            Assert.Equal("{requestId}", stored.StaticHeaders["MS-RequestId"]);
            Assert.NotEqual(initial.EvidenceHash, stored.EvidenceHash);
            Assert.Single(await repository.ListAsync(installationId, CancellationToken.None));
        }
    }
}
