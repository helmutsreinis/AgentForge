using System.Formats.Tar;
using System.Text;
using AgentForge.Abstractions.Agents;
using AgentForge.Abstractions.Artifacts;
using AgentForge.Abstractions.Installations;
using AgentForge.Abstractions.Learning;
using AgentForge.Abstractions.Persistence;
using AgentForge.Abstractions.Providers;
using AgentForge.Abstractions.Security;
using AgentForge.Abstractions.Setup;
using AgentForge.Abstractions.Skills;
using AgentForge.Audit;
using AgentForge.Domain.Agents;
using AgentForge.Domain.Learning;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Providers;
using AgentForge.Domain.Security;
using AgentForge.Domain.Setup;
using AgentForge.Domain.Skills;
using AgentForge.Learning;
using AgentForge.Models;
using AgentForge.Persistence;
using AgentForge.Security;
using AgentForge.Setup;
using AgentForge.Skills;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace AgentForge.IntegrationTests;

public sealed class LocalModelGeneratedApiSkillLiveTests(ITestOutputHelper output) : IDisposable
{
    private const string EndpointVariable = "AGENTFORGE_LIVE_SKILL_GENERATION_ENDPOINT";
    private const string ModelVariable = "AGENTFORGE_LIVE_SKILL_GENERATION_MODEL";
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), $"agentforge-live-skill-generation-{Guid.NewGuid():N}");

    [LocalSkillGenerationFact]
    public async Task AgentForge_uses_local_ai_to_build_and_evaluate_partner_center_customer_skill()
    {
        var endpoint = new Uri(System.Environment.GetEnvironmentVariable(EndpointVariable)!, UriKind.Absolute);
        var model = System.Environment.GetEnvironmentVariable(ModelVariable) ?? "qwen3.8";
        await using var services = BuildServices();
        var installationId = new InstallationId(Guid.NewGuid());
        var providerId = ProviderProfileId.New();
        var agentId = new AgentIdentityId(Guid.NewGuid());
        var roles = Roles();
        await using (var setup = services.CreateAsyncScope())
        {
            await setup.ServiceProvider.GetRequiredService<IDatabaseInitializer>()
                .InitializeAsync(CancellationToken.None);
            var begun = await setup.ServiceProvider.GetRequiredService<ISetupApplicationService>().BeginAsync(
                new BeginSetupRequest(installationId, roles.Worker, new CorrelationId("live-ai-setup")),
                CancellationToken.None);
            Assert.True(begun.IsSuccess, begun.Failure?.Message);
            var now = DateTimeOffset.UtcNow;
            await setup.ServiceProvider.GetRequiredService<IProviderProfileRepository>().AddAsync(new ProviderProfile(
                providerId, installationId, "local-skill-author", "openai-compatible", endpoint, model,
                SecretReference.NoCredential,
                new ProviderCapabilitySummary(true, true, false, false, "live-local-model"),
                0, now, now, roles.Worker, new CorrelationId("live-ai-provider")), CancellationToken.None);
            await setup.ServiceProvider.GetRequiredService<IAgentIdentityRepository>().AddAsync(new AgentIdentity(
                agentId, installationId, "skill-author", "API workflow author", "Build governed skills", "en",
                "Europe/Kiev", "precise", null,
                new AgentModelPolicy(providerId, ModelDataLocality.LocalOnly, false),
                new AgentMemoryPolicy(AgentMemoryScope.Agent, 30),
                new AgentCapabilityPolicy(NetworkPosture.Denied, [], []),
                new AgentBudget(8, 0, 131_072, 12_288, 180),
                new ChildAgentLimits(0, 0, 0, 0),
                new AgentLearningPolicy(LearningMode.Propose, MutableSkillScope.ProposalWorkspaceOnly),
                0, now, now, roles.Worker, new CorrelationId("live-ai-agent")), CancellationToken.None);
            Assert.True((await setup.ServiceProvider.GetRequiredService<IUnitOfWork>()
                .CommitAsync(CancellationToken.None)).Succeeded);
        }

        await using var scope = services.CreateAsyncScope();
        var signalId = new LearningSignalId(Guid.NewGuid());
        var captured = await scope.ServiceProvider.GetRequiredService<ILearningGovernanceService>().CaptureAsync(
            new CaptureLearningSignalRequest(
                signalId, installationId, LearningSignalKind.MissingCapability,
                "The agent needs a governed procedure to discover Microsoft Partner Center customers and fetch one customer by tenant identifier through a configured read-only API profile.",
                "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                [], [], [], 1, roles.Worker, new CorrelationId("live-ai-signal"), null),
            CancellationToken.None);
        Assert.True(captured.IsSuccess, captured.Failure?.Message);

        var generated = await scope.ServiceProvider.GetRequiredService<ILocalModelSkillCandidateGenerator>()
            .GenerateAsync(new GenerateNewSkillFromSignalRequest(
                new LearningCandidateId(Guid.NewGuid()),
                new SkillProposalId(Guid.NewGuid()),
                signalId,
                new SkillId("skill:microsoft.partner-center.customers"),
                new SkillVersion("0.1.0"),
                "Discover Partner Center customers and fetch one exact customer through a configured generic API profile.",
                ["partner-center:customers:read"],
                roles,
                agentId,
                "Use profile microsoft-partner-center. List with relative path customers and a bounded size query. Fetch one customer with relative path customers/{tenant-guid}. Required non-secret Partner Center headers belong to the profile. Never ask for or mention a bearer token. Stop when a tenant identifier is malformed, authority is denied, or response evidence is unavailable.",
                ["tool:http-api.get"]), CancellationToken.None);

        Assert.True(generated.IsSuccess, generated.Failure?.Message);
        var package = await scope.ServiceProvider.GetRequiredService<ISkillRegistryRepository>().FindAsync(
            installationId, generated.Value.Candidate.SkillId, generated.Value.Candidate.CandidateVersion,
            CancellationToken.None);
        Assert.NotNull(package);
        Assert.Equal(["tool:http-api.get"], package.Package.Requirements.ToolIds);
        Assert.Equal(["partner-center:customers:read"], package.Package.Permissions);
        var markdown = await ReadWorkspaceFileAsync(
            scope.ServiceProvider.GetRequiredService<IArtifactStore>(),
            generated.Value.Candidate.ProposalWorkspace, "SKILL.md");
        Assert.Contains("microsoft-partner-center", markdown,
            StringComparison.OrdinalIgnoreCase);

        var evaluated = await scope.ServiceProvider.GetRequiredService<ILearningCandidateEvaluator>()
            .EvaluateAsync(generated.Value.Candidate.Id, 0, CancellationToken.None);
        Assert.True(evaluated.IsSuccess, evaluated.Failure?.Message);
        output.WriteLine($"CandidateId={generated.Value.Candidate.Id.Value:D}");
        output.WriteLine($"Model={generated.Value.Evidence.Model}");
        output.WriteLine($"ModelEvidence={generated.Value.Evidence.ModelEvidenceHash}");
        output.WriteLine($"SelectedMarkdown={generated.Value.Evidence.SelectedMarkdownHash}");
        output.WriteLine($"Package={generated.Value.Candidate.CandidatePackageHash}");
        output.WriteLine($"Evaluation={evaluated.Value.Receipt.Evidence.ContentHash}");
        Assert.All(evaluated.Value.Receipt.Checks, check => Assert.True(check.Passed, check.Summary));
    }

    private ServiceProvider BuildServices()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AgentForge:Installation:DataDirectory"] = _directory,
            ["AgentForge:Persistence:DatabaseFileName"] = "live-skill-generation.db",
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
        services.AddAgentForgeModels();
        services.AddAgentForgeLearning();
        return services.BuildServiceProvider(validateScopes: true);
    }

    private static async Task<string> ReadWorkspaceFileAsync(
        IArtifactStore artifacts,
        AgentForge.Domain.Artifacts.ArtifactReference workspace,
        string name)
    {
        await using var source = await artifacts.OpenReadAsync(workspace, CancellationToken.None);
        using var reader = new TarReader(source, leaveOpen: false);
        TarEntry? entry;
        while ((entry = reader.GetNextEntry(copyData: false)) is not null)
        {
            if (!string.Equals(entry.Name, name, StringComparison.Ordinal) || entry.DataStream is null) continue;
            using var text = new StreamReader(entry.DataStream, new UTF8Encoding(false, true));
            return await text.ReadToEndAsync(CancellationToken.None);
        }
        throw new InvalidOperationException($"Generated workspace did not contain {name}.");
    }

    private static LearningRoleAssignments Roles() => new(
        new ActorId("learning-worker"), new ActorId("learning-proposer"),
        new ActorId("learning-verifier"), new ActorId("learning-critic"),
        new ActorId("learning-governor"));

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}

internal sealed class LocalSkillGenerationFactAttribute : FactAttribute
{
    public LocalSkillGenerationFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(System.Environment.GetEnvironmentVariable(
                "AGENTFORGE_LIVE_SKILL_GENERATION_ENDPOINT")))
        {
            Skip = "Set AGENTFORGE_LIVE_SKILL_GENERATION_ENDPOINT and optionally AGENTFORGE_LIVE_SKILL_GENERATION_MODEL to run this local-model gate.";
        }
    }
}
