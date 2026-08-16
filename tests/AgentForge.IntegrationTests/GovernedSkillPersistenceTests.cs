using System.Text.Json;
using AgentForge.Abstractions.Auditing;
using AgentForge.Abstractions.Installations;
using AgentForge.Abstractions.Security;
using AgentForge.Abstractions.Setup;
using AgentForge.Abstractions.Skills;
using AgentForge.Audit;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Setup;
using AgentForge.Domain.Skills;
using AgentForge.Persistence;
using AgentForge.Security;
using AgentForge.Setup;
using AgentForge.Skills;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AgentForge.IntegrationTests;

public sealed class GovernedSkillPersistenceTests : IDisposable
{
    private const string Evidence =
        "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly string[] SupportedSystems = ["windows", "linux"];
    private static readonly string[] RequiredModelCapabilities = ["text-generation"];
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"agentforge-skills-{Guid.NewGuid():N}");

    [Fact]
    public async Task Seed_and_user_skills_share_governance_snapshots_and_atomic_rollback()
    {
        await using var services = BuildServices();
        await InitializeAsync(services);
        var installationId = new InstallationId(Guid.NewGuid());
        var proposer = new ActorId("skill-proposer");
        var approver = new ActorId("skill-governor");
        await BeginInstallationAsync(services, installationId);

        var seedDirectory = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "skills", "seed", "csharp-review"));
        SkillProposal seedPromotion;
        SkillRunSnapshot originalSnapshot;
        await using (var scope = services.CreateAsyncScope())
        {
            var registry = scope.ServiceProvider.GetRequiredService<ISkillRegistryService>();
            var governance = scope.ServiceProvider.GetRequiredService<ISkillGovernanceService>();
            var snapshots = scope.ServiceProvider.GetRequiredService<ISkillSnapshotService>();
            var installed = await registry.InstallAsync(
                installationId, seedDirectory, SkillPackageProvenance.Seed, proposer,
                new CorrelationId("seed-install"), CancellationToken.None);
            Assert.True(installed.IsSuccess, installed.Failure?.Message);
            Assert.False(installed.Value.WasReplay);
            var replay = await registry.InstallAsync(
                installationId, seedDirectory, SkillPackageProvenance.Seed, proposer,
                new CorrelationId("seed-replay"), CancellationToken.None);
            Assert.True(replay.IsSuccess);
            Assert.True(replay.Value.WasReplay);

            seedPromotion = await PromoteAsync(
                governance, installationId, new SkillVersion("1.0.0"), proposer, approver, "seed");
            var created = await snapshots.CreateAsync(
                new SkillRunSnapshotId(Guid.NewGuid()), installationId, [new SkillId("skill:csharp.review")],
                proposer, "run-original", new CorrelationId("snapshot-original"), null, CancellationToken.None);
            Assert.True(created.IsSuccess, created.Failure?.Message);
            originalSnapshot = created.Value;
        }

        var revisionDirectory = CreatePackage("1.1.0", "USER-REVISION-UNIQUE-BODY", ["repository:read", "repository:metadata"]);
        SkillProposal revisionPromotion;
        SkillRunSnapshot revisedSnapshot;
        await using (var scope = services.CreateAsyncScope())
        {
            var registry = scope.ServiceProvider.GetRequiredService<ISkillRegistryService>();
            var governance = scope.ServiceProvider.GetRequiredService<ISkillGovernanceService>();
            var snapshots = scope.ServiceProvider.GetRequiredService<ISkillSnapshotService>();
            var installed = await registry.InstallAsync(
                installationId, revisionDirectory, SkillPackageProvenance.User, proposer,
                new CorrelationId("revision-install"), CancellationToken.None);
            Assert.True(installed.IsSuccess, installed.Failure?.Message);
            revisionPromotion = await PromoteAsync(
                governance, installationId, new SkillVersion("1.1.0"), proposer, approver, "revision");
            Assert.Equal(["repository:metadata"], revisionPromotion.AddedPermissions);

            var created = await snapshots.CreateAsync(
                new SkillRunSnapshotId(Guid.NewGuid()), installationId, [new SkillId("skill:csharp.review")],
                proposer, "run-revised", new CorrelationId("snapshot-revised"), null, CancellationToken.None);
            Assert.True(created.IsSuccess, created.Failure?.Message);
            revisedSnapshot = created.Value;
            Assert.Equal("1.0.0", originalSnapshot.Selections.Single().Version.Value);
            Assert.Equal("1.1.0", revisedSnapshot.Selections.Single().Version.Value);
            Assert.Contains("C# Review", (await snapshots.OpenBodyAsync(
                originalSnapshot.Id, new SkillId("skill:csharp.review"), CancellationToken.None)).Value);
            Assert.Contains("USER-REVISION-UNIQUE-BODY", (await snapshots.OpenBodyAsync(
                revisedSnapshot.Id, new SkillId("skill:csharp.review"), CancellationToken.None)).Value);
        }

        var staleDirectory = CreatePackage("1.2.0", "STALE-CANDIDATE", ["repository:read"]);
        SkillProposal staleCanary;
        await using (var scope = services.CreateAsyncScope())
        {
            var registry = scope.ServiceProvider.GetRequiredService<ISkillRegistryService>();
            var governance = scope.ServiceProvider.GetRequiredService<ISkillGovernanceService>();
            Assert.True((await registry.InstallAsync(
                installationId, staleDirectory, SkillPackageProvenance.AgentProposal, proposer,
                new CorrelationId("stale-install"), CancellationToken.None)).IsSuccess);
            staleCanary = await AdvanceToCanaryAsync(
                governance, installationId, new SkillVersion("1.2.0"), proposer, approver, "stale");

            var rolledBack = await governance.RollbackAsync(
                revisionPromotion.Id, revisionPromotion.Version, Evidence, CancellationToken.None);
            Assert.True(rolledBack.IsSuccess, rolledBack.Failure?.Message);
            Assert.Equal(SkillProposalState.RolledBack, rolledBack.Value.State);

            var stale = await governance.FinishCanaryAsync(
                staleCanary.Id, staleCanary.Version,
                new SkillCanaryReceipt(true, 10, 11, Evidence), CancellationToken.None);
            Assert.False(stale.IsSuccess);
            Assert.Equal(FailureCode.ConcurrencyConflict, stale.Failure?.Code);

            var active = await scope.ServiceProvider.GetRequiredService<ISkillRegistryRepository>()
                .FindActiveAsync(installationId, new SkillId("skill:csharp.review"), CancellationToken.None);
            Assert.Equal("1.0.0", active?.Package.Version.Value);
        }

        var regressingDirectory = CreatePackage("1.3.0", "REGRESSION-CANDIDATE", ["repository:read"]);
        await using (var scope = services.CreateAsyncScope())
        {
            var registry = scope.ServiceProvider.GetRequiredService<ISkillRegistryService>();
            var governance = scope.ServiceProvider.GetRequiredService<ISkillGovernanceService>();
            Assert.True((await registry.InstallAsync(
                installationId, regressingDirectory, SkillPackageProvenance.AgentProposal, proposer,
                new CorrelationId("regression-install"), CancellationToken.None)).IsSuccess);
            var canary = await AdvanceToCanaryAsync(
                governance, installationId, new SkillVersion("1.3.0"), proposer, approver, "regression");
            var result = await governance.FinishCanaryAsync(
                canary.Id, canary.Version,
                new SkillCanaryReceipt(false, 10, 9, Evidence), CancellationToken.None);
            Assert.True(result.IsSuccess, result.Failure?.Message);
            Assert.Equal(SkillProposalState.Quarantined, result.Value.State);
            var candidate = await scope.ServiceProvider.GetRequiredService<ISkillRegistryRepository>().FindAsync(
                installationId, new SkillId("skill:csharp.review"), new SkillVersion("1.3.0"), CancellationToken.None);
            Assert.Equal(SkillPackageStatus.Quarantined, candidate?.Status);
        }

        var archiveDirectory = CreatePackage("2.0.0", "ARCHIVE-CANDIDATE", ["repository:read"]);
        await using (var scope = services.CreateAsyncScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ISkillRegistryService>();
            var installed = await service.InstallAsync(
                installationId, archiveDirectory, SkillPackageProvenance.User, proposer,
                new CorrelationId("archive-install"), CancellationToken.None);
            Assert.True(installed.IsSuccess);
            var archived = await service.SetStatusAsync(
                installationId, installed.Value.Version.Package.Id, installed.Value.Version.Package.Version,
                0, SkillPackageStatus.Archived, proposer, new CorrelationId("archive"), CancellationToken.None);
            Assert.True(archived.IsSuccess);
            var restored = await service.SetStatusAsync(
                installationId, installed.Value.Version.Package.Id, installed.Value.Version.Package.Version,
                1, SkillPackageStatus.Installed, proposer, new CorrelationId("restore"), CancellationToken.None);
            Assert.True(restored.IsSuccess);

            var db = scope.ServiceProvider.GetRequiredService<AgentForgeDbContext>();
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = "SELECT DescriptorJson FROM skill_versions WHERE Version = '1.1.0'";
            await db.Database.OpenConnectionAsync();
            var descriptor = Assert.IsType<string>(await command.ExecuteScalarAsync());
            Assert.DoesNotContain("USER-REVISION-UNIQUE-BODY", descriptor, StringComparison.Ordinal);
        }

        _ = seedPromotion;
    }

    private static async Task<SkillProposal> PromoteAsync(
        ISkillGovernanceService service,
        InstallationId installationId,
        SkillVersion version,
        ActorId proposer,
        ActorId approver,
        string prefix)
    {
        var canary = await AdvanceToCanaryAsync(service, installationId, version, proposer, approver, prefix);
        var promoted = await service.FinishCanaryAsync(
            canary.Id, canary.Version, new SkillCanaryReceipt(true, 10, 11, Evidence), CancellationToken.None);
        Assert.True(promoted.IsSuccess, promoted.Failure?.Message);
        return promoted.Value;
    }

    private static async Task<SkillProposal> AdvanceToCanaryAsync(
        ISkillGovernanceService service,
        InstallationId installationId,
        SkillVersion version,
        ActorId proposer,
        ActorId approver,
        string prefix)
    {
        var proposalId = new SkillProposalId(Guid.NewGuid());
        var correlationId = new CorrelationId($"{prefix}-proposal");
        var created = await service.CreateProposalAsync(
            proposalId, installationId, new SkillId("skill:csharp.review"), version,
            proposer, correlationId, null, CancellationToken.None);
        Assert.True(created.IsSuccess, created.Failure?.Message);
        var replayed = await service.CreateProposalAsync(
            proposalId, installationId, new SkillId("skill:csharp.review"), version,
            proposer, correlationId, null, CancellationToken.None);
        Assert.True(replayed.IsSuccess, replayed.Failure?.Message);
        Assert.Equal(created.Value.Id, replayed.Value.Id);
        Assert.Equal(created.Value.Version, replayed.Value.Version);
        Assert.Equal(created.Value.SnapshotHash, replayed.Value.SnapshotHash);
        Assert.Equal(created.Value.CandidatePackageHash, replayed.Value.CandidatePackageHash);
        Assert.Equal(created.Value.AddedPermissions, replayed.Value.AddedPermissions);
        Assert.Equal(created.Value.RemovedPermissions, replayed.Value.RemovedPermissions);
        var evaluated = await service.EvaluateAsync(
            created.Value.Id, created.Value.Version,
            new SkillEvaluationReceipt(true, true, true, 10, 11, Evidence), CancellationToken.None);
        Assert.True(evaluated.IsSuccess, evaluated.Failure?.Message);
        var approved = await service.ApproveAsync(
            evaluated.Value.Id, evaluated.Value.Version, approver, CancellationToken.None);
        Assert.True(approved.IsSuccess, approved.Failure?.Message);
        var canary = await service.StartCanaryAsync(
            approved.Value.Id, approved.Value.Version, CancellationToken.None);
        Assert.True(canary.IsSuccess, canary.Failure?.Message);
        return canary.Value;
    }

    private ServiceProvider BuildServices()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AgentForge:Installation:DataDirectory"] = _directory,
            ["AgentForge:Persistence:DatabaseFileName"] = "skills.db",
            ["AgentForge:Persistence:EnableConnectionPooling"] = "false",
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAgentForgeSetup(configuration);
        services.AddAgentForgePersistence(configuration);
        services.AddAgentForgeSecurity(configuration);
        services.AddSingleton<ISecretStore, DeterministicSecretStore>();
        services.AddAgentForgeAudit();
        services.AddAgentForgeSkills();
        return services.BuildServiceProvider(validateScopes: true);
    }

    private static async Task InitializeAsync(ServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>().InitializeAsync(CancellationToken.None);
    }

    private static async Task BeginInstallationAsync(ServiceProvider services, InstallationId installationId)
    {
        await using var scope = services.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<ISetupApplicationService>().BeginAsync(
            new BeginSetupRequest(installationId, new ActorId("local-admin"), new CorrelationId("setup")),
            CancellationToken.None);
        Assert.True(result.IsSuccess, result.Failure?.Message);
    }

    private string CreatePackage(string version, string body, IReadOnlyList<string> permissions)
    {
        var directory = Path.Combine(_directory, $"package-{version}");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "SKILL.md"), $"# Review {version}\n\n{body}\n");
        File.WriteAllText(Path.Combine(directory, "skill.harness.json"), JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            id = "skill:csharp.review",
            version,
            description = $"Review revision {version}.",
            dependencies = Array.Empty<object>(),
            requirements = new
            {
                operatingSystems = SupportedSystems,
                modelCapabilities = RequiredModelCapabilities,
                tools = Array.Empty<string>(),
            },
            permissions,
            signature = (object?)null,
        }));
        return directory;
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

}
