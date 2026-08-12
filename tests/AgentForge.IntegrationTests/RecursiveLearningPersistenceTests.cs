using System.Text.Json;
using AgentForge.Abstractions.Artifacts;
using AgentForge.Abstractions.Installations;
using AgentForge.Abstractions.Learning;
using AgentForge.Abstractions.Security;
using AgentForge.Abstractions.Setup;
using AgentForge.Abstractions.Skills;
using AgentForge.Audit;
using AgentForge.Domain.Learning;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Setup;
using AgentForge.Domain.Skills;
using AgentForge.Learning;
using AgentForge.Persistence;
using AgentForge.Security;
using AgentForge.Setup;
using AgentForge.Skills;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AgentForge.IntegrationTests;

public sealed class RecursiveLearningPersistenceTests : IDisposable
{
    private const string HashA = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string HashB = "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string HashC = "sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
    private static readonly string[] SupportedSystems = ["windows", "linux"];
    private static readonly string[] RequiredModelCapabilities = ["text-generation"];
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"agentforge-learning-{Guid.NewGuid():N}");

    [Fact]
    public async Task Corrected_skill_is_governed_persisted_and_rolled_back_while_bundle_remains_pinned()
    {
        await using var services = BuildServices();
        await InitializeAsync(services);
        var installationId = new InstallationId(Guid.NewGuid());
        await BeginInstallationAsync(services, installationId);
        var baselineDirectory = CreatePackage("skill:test.review", "1.0.0", "BASELINE", ["repository:read"]);
        var revisionDirectory = CreatePackage(
            "skill:test.review", "1.1.0", "CORRECTED", ["repository:read", "repository:metadata"]);
        var roles = Roles();
        LearningSignalId signalId;
        LearningCandidateId candidateId;
        SkillProposalId skillProposalId;

        await using (var scope = services.CreateAsyncScope())
        {
            var registry = scope.ServiceProvider.GetRequiredService<ISkillRegistryService>();
            Assert.True((await registry.InstallAsync(
                installationId, baselineDirectory, SkillPackageProvenance.User, roles.Worker,
                new CorrelationId("baseline-install"), CancellationToken.None)).IsSuccess);
            await PromoteAsync(scope.ServiceProvider.GetRequiredService<ISkillGovernanceService>(),
                installationId, new SkillId("skill:test.review"), new SkillVersion("1.0.0"),
                roles.Proposer, roles.Governor, "baseline");
            Assert.True((await registry.InstallAsync(
                installationId, revisionDirectory, SkillPackageProvenance.AgentProposal, roles.Proposer,
                new CorrelationId("candidate-install"), CancellationToken.None)).IsSuccess);

            var learning = scope.ServiceProvider.GetRequiredService<ILearningGovernanceService>();
            signalId = new LearningSignalId(Guid.NewGuid());
            var captured = await learning.CaptureAsync(new CaptureLearningSignalRequest(
                signalId, installationId, LearningSignalKind.Correction,
                "Operator corrected the review procedure after a successful governed run.", HashC,
                [new SkillUsageReceipt(
                    "run-correction", new SkillId("skill:test.review"), new SkillVersion("1.0.0"),
                    (await scope.ServiceProvider.GetRequiredService<ISkillRegistryRepository>().FindActiveAsync(
                        installationId, new SkillId("skill:test.review"), CancellationToken.None))!.Package.PackageHash,
                    true, DateTimeOffset.UtcNow, HashB)], [], 1, roles.Worker,
                new CorrelationId("learning-correction"), null), CancellationToken.None);
            Assert.True(captured.IsSuccess, captured.Failure?.Message);
            Assert.Equal(LearningAction.SkillRevision, captured.Value.Action);

            await using var workspaceBytes = new MemoryStream([1, 2, 3, 4]);
            var workspace = await scope.ServiceProvider.GetRequiredService<IArtifactStore>().PutAsync(
                workspaceBytes, "application/vnd.agentforge.learning-workspace+tar", CancellationToken.None);
            candidateId = new LearningCandidateId(Guid.NewGuid());
            skillProposalId = new SkillProposalId(Guid.NewGuid());
            var proposed = await learning.ProposeAsync(new ProposeLearningCandidateRequest(
                candidateId, signalId, skillProposalId, new SkillId("skill:test.review"),
                new SkillVersion("1.1.0"), workspace, roles), CancellationToken.None);
            Assert.True(proposed.IsSuccess, proposed.Failure?.Message);
            var verified = await learning.VerifyAsync(candidateId, 0, roles.Verifier,
                Evaluation(passed: true), CancellationToken.None);
            Assert.True(verified.IsSuccess, verified.Failure?.Message);
            var critiqued = await learning.CritiqueAsync(candidateId, 1, roles.Critic,
                new LearningCritique(true, [], HashC), CancellationToken.None);
            Assert.True(critiqued.IsSuccess, critiqued.Failure?.Message);
            var approved = await learning.ApproveAsync(candidateId, 2, roles.Governor, CancellationToken.None);
            Assert.True(approved.IsSuccess, approved.Failure?.Message);
            var canary = await learning.StartCanaryAsync(candidateId, 3, roles.Governor, CancellationToken.None);
            Assert.True(canary.IsSuccess, canary.Failure?.Message);
            var promoted = await learning.FinishCanaryAsync(
                candidateId, 4, roles.Governor, true, 10, 11, HashC, CancellationToken.None);
            Assert.True(promoted.IsSuccess, promoted.Failure?.Message);
            Assert.Equal(LearningCandidateState.Promoted, promoted.Value.State);
        }

        await using (var restartScope = services.CreateAsyncScope())
        {
            var repository = restartScope.ServiceProvider.GetRequiredService<ILearningRepository>();
            var persisted = await repository.FindLatestCandidateAsync(candidateId, CancellationToken.None);
            Assert.Equal(LearningCandidateState.Promoted, persisted?.State);
            Assert.Equal(skillProposalId, persisted?.SkillProposalId);
            var learning = restartScope.ServiceProvider.GetRequiredService<ILearningGovernanceService>();
            var rolledBack = await learning.RollbackAsync(
                candidateId, 5, roles.Governor, HashC, CancellationToken.None);
            Assert.True(rolledBack.IsSuccess, rolledBack.Failure?.Message);
            Assert.Equal(LearningCandidateState.RolledBack, rolledBack.Value.State);
            var active = await restartScope.ServiceProvider.GetRequiredService<ISkillRegistryRepository>().FindActiveAsync(
                installationId, new SkillId("skill:test.review"), CancellationToken.None);
            Assert.Equal("1.0.0", active?.Package.Version.Value);
        }

        await VerifyRejectedCandidateAsync(services, installationId, roles, "1.2.0", canaryRegression: false);
        await VerifyRejectedCandidateAsync(services, installationId, roles, "1.3.0", canaryRegression: true);
        await VerifyBundleAsync(services, installationId, roles);
    }

    private async Task VerifyRejectedCandidateAsync(
        ServiceProvider services, InstallationId installationId, LearningRoleAssignments roles,
        string version, bool canaryRegression)
    {
        await using var scope = services.CreateAsyncScope();
        var skillId = new SkillId("skill:test.review");
        var package = CreatePackage(skillId.Value, version, "REGRESSION", ["repository:read"]);
        var registry = scope.ServiceProvider.GetRequiredService<ISkillRegistryService>();
        Assert.True((await registry.InstallAsync(
            installationId, package, SkillPackageProvenance.AgentProposal, roles.Proposer,
            new CorrelationId($"install-{version}"), CancellationToken.None)).IsSuccess);
        var active = (await scope.ServiceProvider.GetRequiredService<ISkillRegistryRepository>()
            .FindActiveAsync(installationId, skillId, CancellationToken.None))!;
        var learning = scope.ServiceProvider.GetRequiredService<ILearningGovernanceService>();
        var signalId = new LearningSignalId(Guid.NewGuid());
        Assert.True((await learning.CaptureAsync(new CaptureLearningSignalRequest(
            signalId, installationId, LearningSignalKind.Correction, "A deterministic correction receipt.", HashC,
            [new SkillUsageReceipt("run-regression", skillId, active.Package.Version, active.Package.PackageHash,
                true, DateTimeOffset.UtcNow, HashB)], [], 1, roles.Worker,
            new CorrelationId($"signal-{version}"), null), CancellationToken.None)).IsSuccess);
        await using var bytes = new MemoryStream([5, 6, 7]);
        var workspace = await scope.ServiceProvider.GetRequiredService<IArtifactStore>().PutAsync(
            bytes, "application/vnd.agentforge.learning-workspace+tar", CancellationToken.None);
        var candidateId = new LearningCandidateId(Guid.NewGuid());
        Assert.True((await learning.ProposeAsync(new ProposeLearningCandidateRequest(
            candidateId, signalId, new SkillProposalId(Guid.NewGuid()), skillId,
            new SkillVersion(version), workspace, roles), CancellationToken.None)).IsSuccess);
        var verified = await learning.VerifyAsync(candidateId, 0, roles.Verifier,
            Evaluation(passed: canaryRegression), CancellationToken.None);
        Assert.True(verified.IsSuccess, verified.Failure?.Message);
        if (!canaryRegression)
        {
            Assert.Equal(LearningCandidateState.Rejected, verified.Value.State);
            return;
        }

        Assert.True((await learning.CritiqueAsync(candidateId, 1, roles.Critic,
            new LearningCritique(true, [], HashC), CancellationToken.None)).IsSuccess);
        Assert.True((await learning.ApproveAsync(candidateId, 2, roles.Governor, CancellationToken.None)).IsSuccess);
        Assert.True((await learning.StartCanaryAsync(candidateId, 3, roles.Governor, CancellationToken.None)).IsSuccess);
        var quarantined = await learning.FinishCanaryAsync(
            candidateId, 4, roles.Governor, false, 10, 9, HashC, CancellationToken.None);
        Assert.True(quarantined.IsSuccess, quarantined.Failure?.Message);
        Assert.Equal(LearningCandidateState.Quarantined, quarantined.Value.State);
    }

    private async Task VerifyBundleAsync(
        ServiceProvider services, InstallationId installationId, LearningRoleAssignments roles)
    {
        await using var scope = services.CreateAsyncScope();
        var registry = scope.ServiceProvider.GetRequiredService<ISkillRegistryService>();
        RegisteredSkillVersion[] versions = new RegisteredSkillVersion[2];
        var ids = new[] { new SkillId("skill:test.build"), new SkillId("skill:test.verify") };
        for (var index = 0; index < ids.Length; index++)
        {
            var installed = await registry.InstallAsync(
                installationId,
                CreatePackage(ids[index].Value, "1.0.0", $"BODY-{index}", ["repository:read"]),
                SkillPackageProvenance.AgentProposal, roles.Proposer,
                new CorrelationId($"bundle-install-{index}"), CancellationToken.None);
            Assert.True(installed.IsSuccess, installed.Failure?.Message);
            versions[index] = installed.Value.Version;
        }

        var learning = scope.ServiceProvider.GetRequiredService<ILearningGovernanceService>();
        var signalId = new LearningSignalId(Guid.NewGuid());
        var chain = new[]
        {
            new SkillChainStep(0, ids[0], versions[0].Package.Version, versions[0].Package.PackageHash,
                HashA, HashB),
            new SkillChainStep(1, ids[1], versions[1].Package.Version, versions[1].Package.PackageHash,
                HashB, HashC),
        };
        var captured = await learning.CaptureAsync(new CaptureLearningSignalRequest(
            signalId, installationId, LearningSignalKind.RepeatedSkillChain,
            "The same successful governed build and verification chain completed three times.",
            HashC, [], chain, 3, roles.Worker, new CorrelationId("bundle-signal"), null),
            CancellationToken.None);
        Assert.Equal(LearningAction.Bundle, captured.Value.Action);
        var synthesized = await learning.SynthesizeBundleAsync(new SynthesizeSkillBundleRequest(
            new SkillBundleId("bundle:test.release"), new SkillVersion("1.0.0"), signalId,
            versions.ToDictionary(item => item.Package.Id, item => item.Package.Permissions),
            10, 11, true, true, HashC), CancellationToken.None);
        Assert.True(synthesized.IsSuccess, synthesized.Failure?.Message);
        Assert.True(SkillBundleSynthesizer.IsConsistent(synthesized.Value));
        Assert.NotNull(await scope.ServiceProvider.GetRequiredService<ILearningRepository>().FindBundleAsync(
            synthesized.Value.Id, synthesized.Value.Version, CancellationToken.None));
    }

    private static async Task PromoteAsync(
        ISkillGovernanceService service, InstallationId installationId, SkillId skillId, SkillVersion version,
        ActorId proposer, ActorId approver, string prefix)
    {
        var created = await service.CreateProposalAsync(
            new SkillProposalId(Guid.NewGuid()), installationId, skillId, version, proposer,
            new CorrelationId($"{prefix}-proposal"), null, CancellationToken.None);
        Assert.True(created.IsSuccess, created.Failure?.Message);
        var evaluated = await service.EvaluateAsync(created.Value.Id, 0,
            new SkillEvaluationReceipt(true, true, true, 10, 11, HashC), CancellationToken.None);
        Assert.True(evaluated.IsSuccess, evaluated.Failure?.Message);
        var approved = await service.ApproveAsync(created.Value.Id, 1, approver, CancellationToken.None);
        Assert.True(approved.IsSuccess, approved.Failure?.Message);
        var canary = await service.StartCanaryAsync(created.Value.Id, 2, CancellationToken.None);
        Assert.True(canary.IsSuccess, canary.Failure?.Message);
        var promoted = await service.FinishCanaryAsync(created.Value.Id, 3,
            new SkillCanaryReceipt(true, 10, 11, HashC), CancellationToken.None);
        Assert.True(promoted.IsSuccess, promoted.Failure?.Message);
    }

    private ServiceProvider BuildServices()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AgentForge:Installation:DataDirectory"] = _directory,
            ["AgentForge:Persistence:DatabaseFileName"] = "learning.db",
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
        services.AddAgentForgeLearning();
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

    private string CreatePackage(
        string id, string version, string body, IReadOnlyList<string> permissions)
    {
        var directory = Path.Combine(_directory, $"package-{id.Replace(':', '-')}-{version}");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "SKILL.md"), $"# {id}\n\n{body}\n");
        File.WriteAllText(Path.Combine(directory, "skill.harness.json"), JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            id,
            version,
            description = $"Test package {id} {version}.",
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

    private static LearningRoleAssignments Roles() => new(
        new ActorId("learning-worker"), new ActorId("learning-proposer"),
        new ActorId("learning-verifier"), new ActorId("learning-critic"),
        new ActorId("learning-governor"));

    private static LearningCandidateEvaluation Evaluation(bool passed) =>
        new(passed, passed, passed, passed, 10, passed ? 11 : 9, HashC);

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
