using AgentForge.Abstractions.Agents;
using AgentForge.Abstractions.Installations;
using AgentForge.Abstractions.Models;
using AgentForge.Abstractions.Persistence;
using AgentForge.Abstractions.Providers;
using AgentForge.Domain.Agents;
using AgentForge.Domain.Installations;
using AgentForge.Domain.Models;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Providers;
using AgentForge.Domain.Security;
using AgentForge.Models;
using AgentForge.Persistence;
using AgentForge.Security;
using AgentForge.Setup;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AgentForge.IntegrationTests;

public sealed class ModelRouteAuthoritySnapshotReaderTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 8, 11, 16, 0, 0, TimeSpan.Zero);
    private static readonly InstallationId InstallationId = new(
        Guid.Parse("e45adf55-18dd-45f2-9064-7c5c2b757b05"));
    private static readonly AgentIdentityId AgentId = new(
        Guid.Parse("60992f5f-a83b-4734-8d98-0151a251f92b"));
    private static readonly ProviderProfileId ProviderId = new(
        Guid.Parse("861745e5-7408-4310-879c-2d8fa97c043f"));
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"agentforge-route-authority-{Guid.NewGuid():N}");
    private ServiceProvider? _services;

    [Fact]
    public async Task Reads_installation_agent_and_provider_profiles_in_one_durable_snapshot()
    {
        await SeedAsync();
        await using var scope = Services.CreateAsyncScope();
        var reader = scope.ServiceProvider.GetRequiredService<IModelRouteAuthoritySnapshotReader>();

        var result = await reader.ReadAsync(InstallationId, AgentId, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Failure?.Message);
        Assert.Equal(InstallationState.Ready, result.Value.Installation.State);
        Assert.Equal(7, result.Value.Installation.Version);
        Assert.Equal(AgentId, result.Value.Agent.Id);
        Assert.Equal(3, result.Value.Agent.Version);
        var profile = Assert.Single(result.Value.ProviderProfiles);
        Assert.Equal(ProviderId, profile.Id);
        Assert.Equal(4, profile.Version);
        Assert.Equal("authority-model", profile.Model);
    }

    [Fact]
    public async Task Wrong_installation_or_agent_identity_returns_fixed_policy_denial()
    {
        await SeedAsync();
        await using var scope = Services.CreateAsyncScope();
        var reader = scope.ServiceProvider.GetRequiredService<IModelRouteAuthoritySnapshotReader>();

        var wrongInstallation = await reader.ReadAsync(
            new InstallationId(Guid.Parse("2b33a69e-a9f7-45a8-b7dc-a9e77c47ed13")),
            AgentId,
            CancellationToken.None);
        var wrongAgent = await reader.ReadAsync(
            InstallationId,
            new AgentIdentityId(Guid.Parse("790e2264-a5e1-4dc9-8062-ad8d71cb90b2")),
            CancellationToken.None);

        Assert.Equal(FailureCode.PolicyDenied, wrongInstallation.Failure?.Code);
        Assert.Equal(FailureCode.PolicyDenied, wrongAgent.Failure?.Code);
        Assert.Equal(wrongInstallation.Failure?.Message, wrongAgent.Failure?.Message);
    }

    [Fact]
    public async Task Scoped_planner_uses_durable_authority_prepared_context_and_current_health()
    {
        await SeedAsync();
        await using var scope = Services.CreateAsyncScope();
        var planner = scope.ServiceProvider.GetRequiredService<IModelRoutePlanner>();
        var request = new ModelRequest(
            new ModelRequestId(Guid.Parse("60cc0ea7-3c76-4516-9201-bdd4ab454900")),
            "authority-model",
            [new ModelMessage(ModelMessageRole.User, [new ModelTextContent("safe integration input")])],
            [],
            new ModelResponseFormat(ModelResponseFormatKind.Text),
            new ModelInvocationLimits(100, 0, 32, 30),
            0,
            1,
            42,
            new CorrelationId("authority-plan"));

        var result = await planner.PlanAsync(new ModelRoutePlanningRequest(
            InstallationId,
            7,
            AgentId,
            3,
            request,
            100,
            []), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Failure?.Message);
        Assert.Equal(ProviderId, result.Value.Route.ProfileId);
        Assert.Equal(4, result.Value.ProviderVersion);
        Assert.Equal(ModelContextPreparer.PolicyName, result.Value.ContextPreparationPolicy);
        Assert.Equal(71, result.Value.PlanEvidenceHash.Length);
    }

    public async Task InitializeAsync()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AgentForge:Installation:DataDirectory"] = _directory,
                ["AgentForge:Persistence:DatabaseFileName"] = "agentforge.db",
                ["AgentForge:Persistence:EnableConnectionPooling"] = "false",
            })
            .Build();
        var services = new ServiceCollection();
        var runtimeNow = DateTimeOffset.UtcNow;
        services.AddLogging();
        services.AddAgentForgeSetup(configuration);
        services.AddAgentForgePersistence(configuration);
        services.AddAgentForgeSecurity(configuration);
        services.AddAgentForgeModels();
        services.AddSingleton<IModelProviderCatalog>(_ => ModelProviderCatalog.Create([
            new FakeProvider(Descriptor(runtimeNow)),
        ]).Value);
        services.AddSingleton<IModelProviderHealthSource>(_ => ModelProviderHealthCatalog.Create([
            new ModelProviderHealthEvidence(
                ProviderId,
                ModelProviderHealthStatus.Healthy,
                ModelHealthEvidenceSource.Probed,
                0,
                "probe-ok",
                runtimeNow.AddMinutes(-1),
                runtimeNow.AddMinutes(1)),
        ]).Value);
        _services = services.BuildServiceProvider(validateScopes: true);
        await using var scope = Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>()
            .InitializeAsync(CancellationToken.None);
    }

    public Task DisposeAsync()
    {
        _services?.Dispose();
        if (Directory.Exists(_directory))
        {
            var fullPath = Path.GetFullPath(_directory);
            var temporaryRoot = Path.GetFullPath(Path.GetTempPath());
            if (!fullPath.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(fullPath).StartsWith(
                    "agentforge-route-authority-",
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Refusing to remove an unsafe route-authority fixture directory.");
            }

            Directory.Delete(fullPath, recursive: true);
        }

        return Task.CompletedTask;
    }

    private ServiceProvider Services => _services ??
        throw new InvalidOperationException("The test service provider has not been initialized.");

    private async Task SeedAsync()
    {
        await using (var installationScope = Services.CreateAsyncScope())
        {
            var installation = new InstallationSnapshot(
                InstallationId,
                InstallationState.Ready,
                7,
                Now,
                new ActorId("operator"),
                new CorrelationId("authority-installation"),
                null);
            await installationScope.ServiceProvider.GetRequiredService<IInstallationRepository>()
                .AddAsync(installation, CancellationToken.None);
            Assert.True((await installationScope.ServiceProvider.GetRequiredService<IUnitOfWork>()
                .CommitAsync(CancellationToken.None)).Succeeded);
        }

        await using var identityScope = Services.CreateAsyncScope();
        var profile = new ProviderProfile(
            ProviderId,
            InstallationId,
            "authority-provider",
            "authority-fixture",
            new Uri("https://models.example.test/v1/chat/completions"),
            "authority-model",
            new SecretReference("fixture", "provider/authority"),
            new ProviderCapabilitySummary(true, true, true, false, "probed"),
            4,
            Now.AddDays(-1),
            Now,
            new ActorId("operator"),
            new CorrelationId("authority-provider"));
        var agent = new AgentIdentity(
            AgentId,
            InstallationId,
            "authority-agent",
            null,
            null,
            "en",
            "Europe/Kiev",
            "concise",
            null,
            new AgentModelPolicy(ProviderId, ModelDataLocality.CloudAllowed, false),
            new AgentMemoryPolicy(AgentMemoryScope.Task, 30),
            new AgentCapabilityPolicy(NetworkPosture.Denied, [], []),
            new AgentBudget(8, 4, 4_096, 1_024, 60),
            new ChildAgentLimits(1, 1, 1, 1_024),
            new AgentLearningPolicy(LearningMode.Off, MutableSkillScope.None),
            3,
            Now.AddDays(-1),
            Now,
            new ActorId("operator"),
            new CorrelationId("authority-agent"));
        await identityScope.ServiceProvider.GetRequiredService<IProviderProfileRepository>()
            .AddAsync(profile, CancellationToken.None);
        await identityScope.ServiceProvider.GetRequiredService<IAgentIdentityRepository>()
            .AddAsync(agent, CancellationToken.None);
        Assert.True((await identityScope.ServiceProvider.GetRequiredService<IUnitOfWork>()
            .CommitAsync(CancellationToken.None)).Succeeded);
    }

    private static ModelProviderDescriptor Descriptor(DateTimeOffset observedAt) => new(
        ProviderId,
        "authority-fixture",
        "authority-model",
        [
            Capability(ModelCapability.TextGeneration, observedAt),
            Capability(ModelCapability.Streaming, observedAt),
        ],
        new ModelProviderRoutingEvidence(
            ModelProviderDataLocation.Cloud,
            ModelCapabilityEvidenceSource.PolicyApproved,
            8_192,
            1_024,
            9_500,
            1,
            2,
            200,
            observedAt.AddMinutes(-5),
            observedAt.AddMinutes(5)));

    private static ModelCapabilityEvidence Capability(
        ModelCapability capability,
        DateTimeOffset observedAt) => new(
        capability,
        ModelCapabilityEvidenceSource.Probed,
        ModelCapabilityAvailability.Available,
        "Current integration evidence.",
        observedAt.AddMinutes(-5),
        observedAt.AddMinutes(5));

    private sealed class FakeProvider(ModelProviderDescriptor descriptor) : IModelProvider
    {
        public ModelProviderDescriptor Descriptor { get; } = descriptor;

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

}
