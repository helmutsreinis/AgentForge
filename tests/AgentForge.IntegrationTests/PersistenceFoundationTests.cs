using System.Text;
using AgentForge.Abstractions.Agents;
using AgentForge.Abstractions.Artifacts;
using AgentForge.Abstractions.Auditing;
using AgentForge.Abstractions.Environments;
using AgentForge.Abstractions.Installations;
using AgentForge.Abstractions.Persistence;
using AgentForge.Abstractions.Providers;
using AgentForge.Abstractions.Security;
using AgentForge.Abstractions.Setup;
using AgentForge.Audit;
using AgentForge.Domain.Agents;
using AgentForge.Domain.Auditing;
using AgentForge.Domain.Environments;
using AgentForge.Domain.Installations;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Providers;
using AgentForge.Domain.Security;
using AgentForge.Domain.Setup;
using AgentForge.Environment;
using AgentForge.Persistence;
using AgentForge.Security;
using AgentForge.Setup;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AgentForge.IntegrationTests;

public sealed class PersistenceFoundationTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 10, 16, 0, 0, TimeSpan.Zero);
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"agentforge-persistence-{Guid.NewGuid():N}");
    private readonly ServiceProvider _services;

    public PersistenceFoundationTests()
    {
        _services = BuildServices(_directory, "agentforge.db");
    }

    private static ServiceProvider BuildServices(
        string directory,
        string databaseFileName,
        Action<IServiceCollection>? configure = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AgentForge:Installation:DataDirectory"] = directory,
                ["AgentForge:Persistence:DatabaseFileName"] = databaseFileName,
                ["AgentForge:Persistence:EnableConnectionPooling"] = "false",
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAgentForgeSetup(configuration);
        services.AddAgentForgePersistence(configuration);
        services.AddAgentForgeSecurity(configuration);
        services.AddSingleton<ISecretStore, DeterministicSecretStore>();
        services.AddAgentForgeAudit();
        services.AddAgentForgeEnvironment(configuration);
        configure?.Invoke(services);
        return services.BuildServiceProvider(validateScopes: true);
    }

    [Fact]
    public async Task Installation_and_hash_chained_audit_survive_new_scopes()
    {
        await InitializeAsync();
        var installation = InstallationSnapshot.CreateUninitialized(
            new InstallationId(Guid.Parse("f739dc0b-1b05-487f-a2a7-41bd091bfa5e")),
            Now,
            new ActorId("operator"),
            new CorrelationId("setup-1"));

        AuditEventRecord first;
        await using (var scope = _services.CreateAsyncScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IInstallationRepository>();
            var audit = scope.ServiceProvider.GetRequiredService<IAuditSink>();
            await repository.AddAsync(installation, CancellationToken.None);
            first = await audit.AppendAsync(CreateAudit(installation.Id, "installation.created", "setup-1"), CancellationToken.None);
            var commit = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().CommitAsync(CancellationToken.None);
            Assert.True(commit.Succeeded);
            Assert.Equal(2, commit.AffectedRows);
        }

        AuditEventRecord second;
        await using (var scope = _services.CreateAsyncScope())
        {
            var persisted = await scope.ServiceProvider.GetRequiredService<IInstallationRepository>()
                .ReadAsync(CancellationToken.None);
            Assert.Equal(installation, persisted);

            second = await scope.ServiceProvider.GetRequiredService<IAuditSink>()
                .AppendAsync(CreateAudit(installation.Id, "installation.inspected", "setup-2"), CancellationToken.None);
            var commit = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().CommitAsync(CancellationToken.None);
            Assert.True(commit.Succeeded);
        }

        Assert.Equal(1, first.Sequence);
        Assert.Equal(2, second.Sequence);
        Assert.Equal(first.EventHash, second.PreviousHash);
        Assert.NotEqual(first.EventHash, second.EventHash);
    }

    [Fact]
    public async Task Stale_installation_update_returns_typed_concurrency_conflict()
    {
        await InitializeAsync();
        var initial = InstallationSnapshot.CreateUninitialized(
            InstallationId.New(),
            Now,
            new ActorId("operator"),
            new CorrelationId("setup-initial"));
        await using (var seedScope = _services.CreateAsyncScope())
        {
            await seedScope.ServiceProvider.GetRequiredService<IInstallationRepository>()
                .AddAsync(initial, CancellationToken.None);
            Assert.True((await seedScope.ServiceProvider.GetRequiredService<IUnitOfWork>()
                .CommitAsync(CancellationToken.None)).Succeeded);
        }

        await using var firstScope = _services.CreateAsyncScope();
        await using var secondScope = _services.CreateAsyncScope();
        var firstRepository = firstScope.ServiceProvider.GetRequiredService<IInstallationRepository>();
        var secondRepository = secondScope.ServiceProvider.GetRequiredService<IInstallationRepository>();
        var firstRead = await firstRepository.ReadAsync(CancellationToken.None);
        var secondRead = await secondRepository.ReadAsync(CancellationToken.None);
        var firstUpdate = Transition(firstRead, "first");
        var staleUpdate = Transition(secondRead, "second");

        await firstRepository.UpdateAsync(firstUpdate, firstRead.Version, CancellationToken.None);
        var firstCommit = await firstScope.ServiceProvider.GetRequiredService<IUnitOfWork>().CommitAsync(CancellationToken.None);
        Assert.True(firstCommit.Succeeded);

        await secondRepository.UpdateAsync(staleUpdate, secondRead.Version, CancellationToken.None);
        var staleCommit = await secondScope.ServiceProvider.GetRequiredService<IUnitOfWork>().CommitAsync(CancellationToken.None);
        Assert.False(staleCommit.Succeeded);
        Assert.Equal(FailureCode.ConcurrencyConflict, staleCommit.Failure?.Code);
    }

    [Fact]
    public async Task Artifacts_are_content_addressed_and_idempotent()
    {
        await InitializeAsync();
        var bytes = Encoding.UTF8.GetBytes("durable AgentForge evidence");
        await using var scope = _services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IArtifactStore>();
        await using var firstContent = new MemoryStream(bytes);
        var first = await store.PutAsync(firstContent, "text/plain", CancellationToken.None);
        await using var duplicateContent = new MemoryStream(bytes);
        var duplicate = await store.PutAsync(duplicateContent, "text/plain", CancellationToken.None);
        Assert.Equal(first, duplicate);
        Assert.True((await scope.ServiceProvider.GetRequiredService<IUnitOfWork>()
            .CommitAsync(CancellationToken.None)).Succeeded);

        await using var opened = await store.OpenReadAsync(first, CancellationToken.None);
        using var copy = new MemoryStream();
        await opened.CopyToAsync(copy, CancellationToken.None);
        Assert.Equal(bytes, copy.ToArray());
        Assert.StartsWith("sha256:", first.ContentHash, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cold_backup_restores_installation_and_audit_chain()
    {
        await InitializeAsync();
        var installation = InstallationSnapshot.CreateUninitialized(
            InstallationId.New(),
            Now,
            new ActorId("operator"),
            new CorrelationId("backup-seed"));
        await using (var scope = _services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IInstallationRepository>()
                .AddAsync(installation, CancellationToken.None);
            await scope.ServiceProvider.GetRequiredService<IAuditSink>()
                .AppendAsync(CreateAudit(installation.Id, "backup.seeded", "backup-seed"), CancellationToken.None);
            Assert.True((await scope.ServiceProvider.GetRequiredService<IUnitOfWork>()
                .CommitAsync(CancellationToken.None)).Succeeded);
        }

        var source = Path.Combine(_directory, "agentforge.db");
        var backup = Path.Combine(_directory, "restored.db");
        File.Copy(source, backup, overwrite: false);

        await using var restoredServices = BuildServices(_directory, "restored.db");
        await using var restoredScope = restoredServices.CreateAsyncScope();
        await restoredScope.ServiceProvider.GetRequiredService<IDatabaseInitializer>()
            .InitializeAsync(CancellationToken.None);
        var restoredInstallation = await restoredScope.ServiceProvider
            .GetRequiredService<IInstallationRepository>()
            .ReadAsync(CancellationToken.None);
        var restoredAudit = await restoredScope.ServiceProvider.GetRequiredService<IAuditReader>()
            .ReadAsync(installation.Id, 0, 10, CancellationToken.None);

        Assert.Equal(installation, restoredInstallation);
        var auditEvent = Assert.Single(restoredAudit);
        Assert.Equal(new string('0', 64), auditEvent.PreviousHash);
        Assert.False(string.IsNullOrWhiteSpace(auditEvent.EventHash));
    }

    [Fact]
    public async Task Audit_recorder_redacts_before_persistence_and_chain_verifies()
    {
        await InitializeAsync();
        const string password = "never-persist-this-password";
        const string providerKey = "sk-" + "1234567890abcdefghijklmnop";
        await using var scope = _services.CreateAsyncScope();
        var recorder = scope.ServiceProvider.GetRequiredService<IAuditRecorder>();

        var recorded = await recorder.RecordAsync(new AuditRecordRequest(
            null,
            new ActorId("operator"),
            new CorrelationId("redaction-integration"),
            null,
            "security.redaction-verified",
            AuditOutcome.Succeeded,
            new { Password = password, Provider = providerKey },
            new { Status = "accepted" },
            null), CancellationToken.None);
        Assert.Equal(2, recorded.InputRedactionCount);
        Assert.True((await scope.ServiceProvider.GetRequiredService<IUnitOfWork>()
            .CommitAsync(CancellationToken.None)).Succeeded);

        var persisted = Assert.Single(await scope.ServiceProvider.GetRequiredService<IAuditReader>()
            .ReadAsync(null, 0, 10, CancellationToken.None));
        Assert.DoesNotContain(password, persisted.Input.Json, StringComparison.Ordinal);
        Assert.DoesNotContain(providerKey, persisted.Input.Json, StringComparison.Ordinal);
        Assert.Equal(2, persisted.Input.Json.Split("[REDACTED]", StringSplitOptions.None).Length - 1);

        var verification = await scope.ServiceProvider.GetRequiredService<IAuditIntegrityVerifier>()
            .VerifyAsync(CancellationToken.None);
        Assert.True(verification.IsValid);
        Assert.Equal(1, verification.VerifiedEventCount);
    }

    [Fact]
    public async Task Setup_application_service_commits_transition_and_audit_atomically()
    {
        await InitializeAsync();
        var installationId = new InstallationId(Guid.Parse("f8f9bb06-45ea-4d61-bb3f-c7fd4254c1e0"));
        var request = new BeginSetupRequest(
            installationId,
            new ActorId("local-operator"),
            new CorrelationId("headless-setup-001"));

        await using (var scope = _services.CreateAsyncScope())
        {
            var result = await scope.ServiceProvider.GetRequiredService<ISetupApplicationService>()
                .BeginAsync(request, CancellationToken.None);
            Assert.True(result.IsSuccess);
            Assert.Equal(InstallationState.Configuring, result.Value.Installation.State);
            Assert.Equal(1, result.Value.Installation.Version);
            Assert.Equal(1, result.Value.AuditEvent.Sequence);
        }

        await using (var verificationScope = _services.CreateAsyncScope())
        {
            var installation = await verificationScope.ServiceProvider.GetRequiredService<IInstallationRepository>()
                .ReadAsync(CancellationToken.None);
            var audit = await verificationScope.ServiceProvider.GetRequiredService<IAuditReader>()
                .ReadAsync(installationId, 0, 10, CancellationToken.None);
            var integrity = await verificationScope.ServiceProvider.GetRequiredService<IAuditIntegrityVerifier>()
                .VerifyAsync(CancellationToken.None);
            Assert.Equal(InstallationState.Configuring, installation.State);
            Assert.Equal("setup.configuration-begun", Assert.Single(audit).OperationType);
            Assert.True(integrity.IsValid);
        }

        await using (var duplicateScope = _services.CreateAsyncScope())
        {
            var duplicate = await duplicateScope.ServiceProvider.GetRequiredService<ISetupApplicationService>()
                .BeginAsync(request, CancellationToken.None);
            Assert.False(duplicate.IsSuccess);
            Assert.Equal(FailureCode.InvalidStateTransition, duplicate.Failure?.Code);
        }
    }

    [Fact]
    public async Task Provider_profile_persists_only_secret_reference_after_deterministic_validation()
    {
        await InitializeAsync();
        var installationId = new InstallationId(Guid.Parse("d032689a-d625-4329-ac55-cdaf201fa834"));
        await using (var beginScope = _services.CreateAsyncScope())
        {
            var begin = await beginScope.ServiceProvider.GetRequiredService<ISetupApplicationService>()
                .BeginAsync(new BeginSetupRequest(
                    installationId,
                    new ActorId("operator"),
                    new CorrelationId("provider-begin")), CancellationToken.None);
            Assert.True(begin.IsSuccess);
        }

        const string plaintext = "provider-" + "credential-value-123456";
        SecretReference secretReference;
        await using (var secretScope = _services.CreateAsyncScope())
        {
            var stored = await secretScope.ServiceProvider.GetRequiredService<ISecretStore>()
                .StoreAsync("primary-provider", plaintext.AsMemory(), CancellationToken.None);
            Assert.True(stored.IsSuccess);
            secretReference = stored.Value;
        }

        await using (var configureScope = _services.CreateAsyncScope())
        {
            var configured = await configureScope.ServiceProvider.GetRequiredService<ISetupApplicationService>()
                .ConfigureProviderAsync(new ConfigureProviderRequest(
                    new ProviderProfileCandidate(
                        "primary",
                        "deterministic",
                        new Uri("http://127.0.0.1:9000/v1"),
                        "deterministic-text-v1",
                        secretReference),
                    new ActorId("operator"),
                    new CorrelationId("provider-configure")), CancellationToken.None);
            Assert.True(configured.IsSuccess);
            Assert.True(configured.Value.Profile.Capabilities.TextGeneration);
            Assert.Equal("deterministic-validation-v1", configured.Value.Profile.Capabilities.EvidenceSource);
        }

        await using (var verificationScope = _services.CreateAsyncScope())
        {
            var profile = await verificationScope.ServiceProvider.GetRequiredService<IProviderProfileRepository>()
                .FindByNameAsync(installationId, "primary", CancellationToken.None);
            Assert.NotNull(profile);
            Assert.Equal(secretReference, profile.SecretReference);
            var auditEvents = await verificationScope.ServiceProvider.GetRequiredService<IAuditReader>()
                .ReadAsync(installationId, 0, 10, CancellationToken.None);
            Assert.Equal(2, auditEvents.Count);
            Assert.DoesNotContain(plaintext, string.Join(string.Empty, auditEvents.Select(item => item.Input.Json)), StringComparison.Ordinal);
        }

        var databaseBytes = await File.ReadAllBytesAsync(Path.Combine(_directory, "agentforge.db"));
        var secretBytes = Encoding.UTF8.GetBytes(plaintext);
        Assert.Equal(-1, databaseBytes.AsSpan().IndexOf(secretBytes));

        File.Copy(
            Path.Combine(_directory, "agentforge.db"),
            Path.Combine(_directory, "provider-restored.db"));
        await using var restoredServices = BuildServices(_directory, "provider-restored.db");
        await using var restoredScope = restoredServices.CreateAsyncScope();
        await restoredScope.ServiceProvider.GetRequiredService<IDatabaseInitializer>()
            .InitializeAsync(CancellationToken.None);
        var restoredProfile = await restoredScope.ServiceProvider.GetRequiredService<IProviderProfileRepository>()
            .FindByNameAsync(installationId, "primary", CancellationToken.None);
        Assert.NotNull(restoredProfile);
        Assert.Equal(secretReference, restoredProfile.SecretReference);
    }

    [Fact]
    public async Task Provider_profile_migration_upgrades_baseline_without_losing_installation()
    {
        await using (var baselineScope = _services.CreateAsyncScope())
        {
            var context = baselineScope.ServiceProvider.GetRequiredService<AgentForgeDbContext>();
            var migrator = context.Database.GetService<IMigrator>();
            await migrator.MigrateAsync("20260810163716_InitialDurableFoundation", CancellationToken.None);
            var installation = InstallationSnapshot.CreateUninitialized(
                new InstallationId(Guid.Parse("ef9216be-b7e3-45a9-bb39-e57f526045c9")),
                Now,
                new ActorId("upgrade-fixture"),
                new CorrelationId("upgrade-baseline"));
            await baselineScope.ServiceProvider.GetRequiredService<IInstallationRepository>()
                .AddAsync(installation, CancellationToken.None);
            Assert.True((await baselineScope.ServiceProvider.GetRequiredService<IUnitOfWork>()
                .CommitAsync(CancellationToken.None)).Succeeded);
        }

        await InitializeAsync();
        await using var upgradedScope = _services.CreateAsyncScope();
        var upgraded = await upgradedScope.ServiceProvider.GetRequiredService<IInstallationRepository>()
            .ReadAsync(CancellationToken.None);
        var provider = await upgradedScope.ServiceProvider.GetRequiredService<IProviderProfileRepository>()
            .FindByNameAsync(upgraded.Id, "missing", CancellationToken.None);
        Assert.Equal("upgrade-fixture", upgraded.ActorId.Value);
        Assert.Null(provider);
    }

    [Fact]
    public async Task Concurrent_duplicate_provider_name_returns_typed_conflict()
    {
        await InitializeAsync();
        var installation = InstallationSnapshot.CreateUninitialized(
            new InstallationId(Guid.Parse("b11a3721-a580-4311-b35d-24336c352507")),
            Now,
            new ActorId("operator"),
            new CorrelationId("provider-race-seed"));
        await using (var seedScope = _services.CreateAsyncScope())
        {
            await seedScope.ServiceProvider.GetRequiredService<IInstallationRepository>()
                .AddAsync(installation, CancellationToken.None);
            Assert.True((await seedScope.ServiceProvider.GetRequiredService<IUnitOfWork>()
                .CommitAsync(CancellationToken.None)).Succeeded);
        }

        await using var firstScope = _services.CreateAsyncScope();
        await using var secondScope = _services.CreateAsyncScope();
        await firstScope.ServiceProvider.GetRequiredService<IProviderProfileRepository>()
            .AddAsync(CreateProviderProfile(installation.Id, ProviderProfileId.New(), "duplicate"), CancellationToken.None);
        await secondScope.ServiceProvider.GetRequiredService<IProviderProfileRepository>()
            .AddAsync(CreateProviderProfile(installation.Id, ProviderProfileId.New(), "duplicate"), CancellationToken.None);

        Assert.True((await firstScope.ServiceProvider.GetRequiredService<IUnitOfWork>()
            .CommitAsync(CancellationToken.None)).Succeeded);
        var conflict = await secondScope.ServiceProvider.GetRequiredService<IUnitOfWork>()
            .CommitAsync(CancellationToken.None);
        Assert.False(conflict.Succeeded);
        Assert.Equal(FailureCode.ConcurrencyConflict, conflict.Failure?.Code);
        Assert.True(conflict.Failure?.IsRetryable);
    }

    [Fact]
    public async Task Agent_preview_is_write_free_and_creation_persists_exact_effective_bounds()
    {
        await InitializeAsync();
        var installationId = new InstallationId(Guid.Parse("27b3ee21-5c8d-4db7-9db6-eac8cf1c605f"));
        ProviderProfile provider;
        await using (var setupScope = _services.CreateAsyncScope())
        {
            var setup = setupScope.ServiceProvider.GetRequiredService<ISetupApplicationService>();
            Assert.True((await setup.BeginAsync(new BeginSetupRequest(
                installationId,
                new ActorId("operator"),
                new CorrelationId("agent-begin")), CancellationToken.None)).IsSuccess);

            var secret = await setupScope.ServiceProvider.GetRequiredService<ISecretStore>()
                .StoreAsync("agent-provider", "fixture-value".AsMemory(), CancellationToken.None);
            Assert.True(secret.IsSuccess);
            var configured = await setup.ConfigureProviderAsync(new ConfigureProviderRequest(
                new ProviderProfileCandidate(
                    "agent-primary",
                    "deterministic",
                    new Uri("http://127.0.0.1:9000/v1"),
                    "deterministic-text-v1",
                    secret.Value),
                new ActorId("operator"),
                new CorrelationId("agent-provider")), CancellationToken.None);
            Assert.True(configured.IsSuccess);
            provider = configured.Value.Profile;
        }

        var candidate = CreateAgentCandidate(provider.Id);
        await using (var previewScope = _services.CreateAsyncScope())
        {
            var preview = await previewScope.ServiceProvider.GetRequiredService<ISetupApplicationService>()
                .PreviewAgentAsync(new PreviewAgentRequest(
                    candidate,
                    new ActorId("operator"),
                    new CorrelationId("agent-preview")), CancellationToken.None);
            Assert.True(preview.IsSuccess);
            Assert.Equal("agent-primary", preview.Value.ProviderName);
            Assert.Equal(
                CapabilityDecision.Deny,
                Assert.Single(preview.Value.Capabilities, item => item.CapabilityId == "network.external").Decision);
            Assert.Null(await previewScope.ServiceProvider.GetRequiredService<IAgentIdentityRepository>()
                .FindByNameAsync(installationId, "Architect", CancellationToken.None));
            Assert.Equal(2, (await previewScope.ServiceProvider.GetRequiredService<IAuditReader>()
                .ReadAsync(installationId, 0, 10, CancellationToken.None)).Count);
        }

        AgentIdentity created;
        await using (var createScope = _services.CreateAsyncScope())
        {
            var result = await createScope.ServiceProvider.GetRequiredService<ISetupApplicationService>()
                .CreateAgentAsync(new CreateAgentRequest(
                    candidate,
                    new ActorId("operator"),
                    new CorrelationId("agent-create")), CancellationToken.None);
            Assert.True(result.IsSuccess);
            created = result.Value.Agent;
            Assert.Equal(LearningMode.Propose, created.LearningPolicy.Mode);
            Assert.Equal(10_000, created.ChildLimits.MaxTotalTokens);
        }

        await using (var verificationScope = _services.CreateAsyncScope())
        {
            var restored = await verificationScope.ServiceProvider.GetRequiredService<IAgentIdentityRepository>()
                .FindByIdAsync(created.Id, CancellationToken.None);
            Assert.NotNull(restored);
            Assert.Equal(candidate.Budget, restored.Budget);
            Assert.Equal(candidate.ChildLimits, restored.ChildLimits);
            Assert.Equal(candidate.ModelPolicy, restored.ModelPolicy);
            Assert.Equal(candidate.MemoryPolicy, restored.MemoryPolicy);
            Assert.Equal(candidate.CapabilityPolicy.NetworkPosture, restored.CapabilityPolicy.NetworkPosture);
            Assert.Equal(candidate.CapabilityPolicy.ToolGrants, restored.CapabilityPolicy.ToolGrants);
            Assert.Equal(candidate.CapabilityPolicy.SkillGrants, restored.CapabilityPolicy.SkillGrants);
            Assert.Equal(candidate.LearningPolicy, restored.LearningPolicy);
            Assert.Equal(3, (await verificationScope.ServiceProvider.GetRequiredService<IAuditReader>()
                .ReadAsync(installationId, 0, 10, CancellationToken.None)).Count);
        }
    }

    [Fact]
    public async Task Agent_migration_upgrades_provider_schema_without_losing_profiles()
    {
        var installationId = new InstallationId(Guid.Parse("81f68955-fc92-4c43-82e8-dc80eadb589a"));
        var providerId = new ProviderProfileId(Guid.Parse("82035fd5-3045-44d9-b97e-c5262074a6ae"));
        await using (var providerSchemaScope = _services.CreateAsyncScope())
        {
            var context = providerSchemaScope.ServiceProvider.GetRequiredService<AgentForgeDbContext>();
            await context.Database.GetService<IMigrator>()
                .MigrateAsync("20260810171602_ProviderProfiles", CancellationToken.None);
            await providerSchemaScope.ServiceProvider.GetRequiredService<IInstallationRepository>()
                .AddAsync(InstallationSnapshot.CreateUninitialized(
                    installationId,
                    Now,
                    new ActorId("upgrade-fixture"),
                    new CorrelationId("agent-upgrade")), CancellationToken.None);
            await providerSchemaScope.ServiceProvider.GetRequiredService<IProviderProfileRepository>()
                .AddAsync(CreateProviderProfile(installationId, providerId, "upgrade-provider"), CancellationToken.None);
            Assert.True((await providerSchemaScope.ServiceProvider.GetRequiredService<IUnitOfWork>()
                .CommitAsync(CancellationToken.None)).Succeeded);
        }

        await InitializeAsync();
        await using var upgradedScope = _services.CreateAsyncScope();
        Assert.NotNull(await upgradedScope.ServiceProvider.GetRequiredService<IProviderProfileRepository>()
            .FindByIdAsync(providerId, CancellationToken.None));
        Assert.Null(await upgradedScope.ServiceProvider.GetRequiredService<IAgentIdentityRepository>()
            .FindByNameAsync(installationId, "missing", CancellationToken.None));
    }

    [Fact]
    public async Task Completion_creates_verified_administrator_and_is_the_only_path_to_ready()
    {
        await InitializeAsync();
        var installationId = new InstallationId(Guid.Parse("11b92a79-31a2-43fc-a0df-b4b3aa803c97"));
        ProviderProfile provider;
        await using (var configurationScope = _services.CreateAsyncScope())
        {
            var setup = configurationScope.ServiceProvider.GetRequiredService<ISetupApplicationService>();
            Assert.True((await setup.BeginAsync(new BeginSetupRequest(
                installationId,
                new ActorId("local-admin"),
                new CorrelationId("complete-begin")), CancellationToken.None)).IsSuccess);
            var secret = await configurationScope.ServiceProvider.GetRequiredService<ISecretStore>()
                .StoreAsync("complete-provider", "provider-fixture".AsMemory(), CancellationToken.None);
            Assert.True(secret.IsSuccess);
            var configuredProvider = await setup.ConfigureProviderAsync(new ConfigureProviderRequest(
                new ProviderProfileCandidate(
                    "primary",
                    "deterministic",
                    new Uri("http://127.0.0.1:9000/v1"),
                    "deterministic-text-v1",
                    secret.Value),
                new ActorId("local-admin"),
                new CorrelationId("complete-provider")), CancellationToken.None);
            Assert.True(configuredProvider.IsSuccess);
            provider = configuredProvider.Value.Profile;
            Assert.True((await setup.CreateAgentAsync(new CreateAgentRequest(
                CreateAgentCandidate(provider.Id),
                new ActorId("local-admin"),
                new CorrelationId("complete-agent")), CancellationToken.None)).IsSuccess);
        }

        LocalAdministrator administrator;
        await using (var completionScope = _services.CreateAsyncScope())
        {
            var completed = await completionScope.ServiceProvider.GetRequiredService<ISetupApplicationService>()
                .CompleteAsync(new CompleteSetupRequest(
                    new ActorId("local-admin"),
                    new CorrelationId("complete-ready")), CancellationToken.None);
            Assert.True(completed.IsSuccess);
            Assert.Equal(InstallationState.Ready, completed.Value.Installation.State);
            Assert.Equal(6, completed.Value.Checks.Count);
            Assert.All(completed.Value.Checks, check => Assert.True(check.Succeeded));
            administrator = completed.Value.Administrator;
        }

        byte[] credentialBytes;
        await using (var verificationScope = _services.CreateAsyncScope())
        {
            var installation = await verificationScope.ServiceProvider.GetRequiredService<IInstallationRepository>()
                .ReadAsync(CancellationToken.None);
            Assert.Equal(InstallationState.Ready, installation.State);
            var restoredAdministrator = await verificationScope.ServiceProvider
                .GetRequiredService<ILocalAdministratorRepository>()
                .FindAsync(installationId, CancellationToken.None);
            Assert.NotNull(restoredAdministrator);
            Assert.Equal(administrator.Id, restoredAdministrator.Id);
            var materialized = await verificationScope.ServiceProvider.GetRequiredService<ISecretStore>()
                .MaterializeAsync(administrator.ClientCredentialReference, CancellationToken.None);
            Assert.True(materialized.IsSuccess);
            await using var credential = materialized.Value;
            Assert.True(verificationScope.ServiceProvider.GetRequiredService<ILocalAdministratorCredentialService>()
                .Verify(credential.Value.Span, administrator.CredentialVerifier));
            var authenticated = await verificationScope.ServiceProvider.GetRequiredService<ILocalAdministratorAuthenticator>()
                .AuthenticateAsync(installationId, credential.Value, CancellationToken.None);
            Assert.True(authenticated.IsSuccess);
            Assert.Equal("local-admin", authenticated.Value.Value);
            var denied = await verificationScope.ServiceProvider.GetRequiredService<ILocalAdministratorAuthenticator>()
                .AuthenticateAsync(installationId, "wrong-credential".AsMemory(), CancellationToken.None);
            Assert.False(denied.IsSuccess);
            Assert.Equal(FailureCode.PolicyDenied, denied.Failure?.Code);
            credentialBytes = Encoding.UTF8.GetBytes(credential.Value.ToArray());
            Assert.Equal(4, (await verificationScope.ServiceProvider.GetRequiredService<IAuditReader>()
                .ReadAsync(installationId, 0, 10, CancellationToken.None)).Count);
        }

        try
        {
            var databaseBytes = await File.ReadAllBytesAsync(Path.Combine(_directory, "agentforge.db"));
            Assert.Equal(-1, databaseBytes.AsSpan().IndexOf(credentialBytes));
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(credentialBytes);
        }

        await using var duplicateScope = _services.CreateAsyncScope();
        var duplicate = await duplicateScope.ServiceProvider.GetRequiredService<ISetupApplicationService>()
            .CompleteAsync(new CompleteSetupRequest(
                new ActorId("local-admin"),
                new CorrelationId("complete-duplicate")), CancellationToken.None);
        Assert.False(duplicate.IsSuccess);
        Assert.Equal(FailureCode.InvalidStateTransition, duplicate.Failure?.Code);
    }

    [Fact]
    public async Task Administrator_migration_preserves_existing_agent_configuration()
    {
        var installationId = new InstallationId(Guid.Parse("e28f2935-578a-4752-bad8-b4759853267f"));
        var providerId = new ProviderProfileId(Guid.Parse("bd4ec746-e8bb-43d6-a514-fd2020dd80b7"));
        var agentId = new AgentIdentityId(Guid.Parse("c657e9c5-66a7-4427-ac99-6ad3fdad16a6"));
        await using (var agentSchemaScope = _services.CreateAsyncScope())
        {
            var context = agentSchemaScope.ServiceProvider.GetRequiredService<AgentForgeDbContext>();
            await context.Database.GetService<IMigrator>()
                .MigrateAsync("20260810173146_AgentIdentities", CancellationToken.None);
            await agentSchemaScope.ServiceProvider.GetRequiredService<IInstallationRepository>()
                .AddAsync(InstallationSnapshot.CreateUninitialized(
                    installationId,
                    Now,
                    new ActorId("upgrade-fixture"),
                    new CorrelationId("administrator-upgrade")), CancellationToken.None);
            await agentSchemaScope.ServiceProvider.GetRequiredService<IProviderProfileRepository>()
                .AddAsync(CreateProviderProfile(installationId, providerId, "upgrade-provider"), CancellationToken.None);
            var candidate = CreateAgentCandidate(providerId);
            await agentSchemaScope.ServiceProvider.GetRequiredService<IAgentIdentityRepository>()
                .AddAsync(new AgentIdentity(
                    agentId,
                    installationId,
                    candidate.Name,
                    candidate.Expertise,
                    candidate.Mission,
                    candidate.PreferredLanguage,
                    candidate.TimeZone,
                    candidate.ResponseStyle,
                    candidate.DefaultWorkspace,
                    candidate.ModelPolicy,
                    candidate.MemoryPolicy,
                    candidate.CapabilityPolicy,
                    candidate.Budget,
                    candidate.ChildLimits,
                    candidate.LearningPolicy,
                    0,
                    Now,
                    Now,
                    new ActorId("upgrade-fixture"),
                    new CorrelationId("administrator-upgrade")), CancellationToken.None);
            Assert.True((await agentSchemaScope.ServiceProvider.GetRequiredService<IUnitOfWork>()
                .CommitAsync(CancellationToken.None)).Succeeded);
        }

        await InitializeAsync();
        await using var upgradedScope = _services.CreateAsyncScope();
        Assert.NotNull(await upgradedScope.ServiceProvider.GetRequiredService<IAgentIdentityRepository>()
            .FindByIdAsync(agentId, CancellationToken.None));
        Assert.Null(await upgradedScope.ServiceProvider.GetRequiredService<ILocalAdministratorRepository>()
            .FindAsync(installationId, CancellationToken.None));
    }

    [Fact]
    public async Task Setup_snapshot_migration_preserves_existing_administrator_configuration()
    {
        var installationId = new InstallationId(Guid.Parse("1b3752f5-371b-46fc-a2cf-5c4402252337"));
        var administrator = new LocalAdministrator(
            new AdministratorIdentityId(Guid.Parse("95ff3663-192b-4560-a67e-8b38441f16a4")),
            installationId,
            new ActorId("upgrade-administrator"),
            new SecretReference("test-memory", "upgrade-administrator-reference"),
            new AdministratorCredentialVerifier("PBKDF2-SHA256", 210_000, "fixture-salt", "fixture-verifier"),
            0,
            Now,
            Now,
            new CorrelationId("snapshot-upgrade"));
        await using (var administratorSchemaScope = _services.CreateAsyncScope())
        {
            var context = administratorSchemaScope.ServiceProvider.GetRequiredService<AgentForgeDbContext>();
            await context.Database.GetService<IMigrator>()
                .MigrateAsync("20260810174842_LocalAdministrator", CancellationToken.None);
            await administratorSchemaScope.ServiceProvider.GetRequiredService<IInstallationRepository>()
                .AddAsync(InstallationSnapshot.CreateUninitialized(
                    installationId,
                    Now,
                    new ActorId("upgrade-fixture"),
                    new CorrelationId("snapshot-upgrade")), CancellationToken.None);
            await administratorSchemaScope.ServiceProvider.GetRequiredService<ILocalAdministratorRepository>()
                .AddAsync(administrator, CancellationToken.None);
            Assert.True((await administratorSchemaScope.ServiceProvider.GetRequiredService<IUnitOfWork>()
                .CommitAsync(CancellationToken.None)).Succeeded);
        }

        await InitializeAsync();
        await using var upgradedScope = _services.CreateAsyncScope();
        Assert.Equal(
            administrator,
            await upgradedScope.ServiceProvider.GetRequiredService<ILocalAdministratorRepository>()
                .FindAsync(installationId, CancellationToken.None));
        Assert.Empty(await upgradedScope.ServiceProvider.GetRequiredService<ISetupProfileSnapshotRepository>()
            .ListAsync(installationId, CancellationToken.None));
    }

    [Fact]
    public async Task Doctor_export_and_authorized_recovery_are_redacted_version_bound_and_restart_safe()
    {
        await InitializeAsync();
        var installationId = new InstallationId(Guid.Parse("4ae83320-910f-4685-b9ca-722ec5bd65bd"));
        await using (var setupScope = _services.CreateAsyncScope())
        {
            var setup = setupScope.ServiceProvider.GetRequiredService<ISetupApplicationService>();
            Assert.True((await setup.BeginAsync(new BeginSetupRequest(
                installationId,
                new ActorId("local-admin"),
                new CorrelationId("maintenance-begin")), CancellationToken.None)).IsSuccess);
            var provider = await setup.ConfigureProviderCredentialAsync(new ConfigureProviderCredentialRequest(
                "primary",
                "deterministic",
                new Uri("http://127.0.0.1:9000/v1"),
                "deterministic-text-v1",
                "provider-fixture".AsMemory(),
                new ActorId("local-admin"),
                new CorrelationId("maintenance-provider")), CancellationToken.None);
            Assert.True(provider.IsSuccess);
            Assert.True((await setup.CreateAgentAsync(new CreateAgentRequest(
                CreateAgentCandidate(provider.Value.Profile.Id),
                new ActorId("local-admin"),
                new CorrelationId("maintenance-agent")), CancellationToken.None)).IsSuccess);
            Assert.True((await setup.CompleteAsync(new CompleteSetupRequest(
                new ActorId("local-admin"),
                new CorrelationId("maintenance-ready")), CancellationToken.None)).IsSuccess);
        }

        Assert.DoesNotContain(
            "provider-fixture",
            Encoding.UTF8.GetString(await File.ReadAllBytesAsync(
                Path.Combine(_directory, "agentforge.db"),
                CancellationToken.None)),
            StringComparison.Ordinal);

        await using var credentialScope = _services.CreateAsyncScope();
        var administrator = await credentialScope.ServiceProvider.GetRequiredService<ILocalAdministratorRepository>()
            .FindAsync(installationId, CancellationToken.None);
        Assert.NotNull(administrator);
        var materialized = await credentialScope.ServiceProvider.GetRequiredService<ISecretStore>()
            .MaterializeAsync(administrator.ClientCredentialReference, CancellationToken.None);
        Assert.True(materialized.IsSuccess);
        await using var credential = materialized.Value;

        await using (var doctorScope = _services.CreateAsyncScope())
        {
            var doctor = await doctorScope.ServiceProvider.GetRequiredService<ISetupMaintenanceService>()
                .DoctorAsync(new DoctorRequest(
                    new ActorId("local-admin"),
                    new CorrelationId("maintenance-doctor")), CancellationToken.None);
            Assert.True(doctor.IsSuccess);
            Assert.True(doctor.Value.IsHealthy);
            Assert.All(doctor.Value.Checks, check => Assert.NotEqual(DoctorCheckStatus.Fail, check.Status));
        }

        ExportSetupProfileResult exported;
        await using (var exportScope = _services.CreateAsyncScope())
        {
            var export = await exportScope.ServiceProvider.GetRequiredService<ISetupMaintenanceService>()
                .ExportAsync(new ExportSetupProfileRequest(
                    3,
                    new ActorId("local-admin"),
                    new CorrelationId("maintenance-export"),
                    credential.Value), CancellationToken.None);
            Assert.True(export.IsSuccess);
            exported = export.Value;
            Assert.Equal(SetupProfileSnapshotKind.SetupReport, exported.Report.Kind);
            Assert.Equal(SetupProfileSnapshotKind.Rollback, exported.Rollback.Kind);
        }

        await using (var artifactScope = _services.CreateAsyncScope())
        {
            var store = artifactScope.ServiceProvider.GetRequiredService<IArtifactStore>();
            await using var reportStream = await store.OpenReadAsync(exported.Report.Artifact, CancellationToken.None);
            using var reportReader = new StreamReader(reportStream, Encoding.UTF8);
            var reportJson = await reportReader.ReadToEndAsync(CancellationToken.None);
            await using var rollbackStream = await store.OpenReadAsync(exported.Rollback.Artifact, CancellationToken.None);
            using var rollbackReader = new StreamReader(rollbackStream, Encoding.UTF8);
            var rollbackJson = await rollbackReader.ReadToEndAsync(CancellationToken.None);
            Assert.Contains("agentforge.setup-report", reportJson, StringComparison.Ordinal);
            Assert.Contains("agentforge.rollback-profile", rollbackJson, StringComparison.Ordinal);
            Assert.Contains(administrator.ClientCredentialReference.Key, rollbackJson, StringComparison.Ordinal);
            Assert.DoesNotContain(administrator.CredentialVerifier.Verifier, rollbackJson, StringComparison.Ordinal);
            Assert.Equal(
                -1,
                rollbackJson.AsSpan().IndexOf(credential.Value.Span, StringComparison.Ordinal));
            Assert.Equal(2, (await artifactScope.ServiceProvider.GetRequiredService<ISetupProfileSnapshotRepository>()
                .ListAsync(installationId, CancellationToken.None)).Count);
        }

        await using (var deniedScope = _services.CreateAsyncScope())
        {
            var denied = await deniedScope.ServiceProvider.GetRequiredService<ISetupMaintenanceService>()
                .ExportAsync(new ExportSetupProfileRequest(
                    3,
                    new ActorId("local-admin"),
                    new CorrelationId("maintenance-denied"),
                    "wrong-credential".AsMemory()), CancellationToken.None);
            Assert.False(denied.IsSuccess);
            Assert.Equal(FailureCode.PolicyDenied, denied.Failure?.Code);

            var deniedWithStaleVersion = await deniedScope.ServiceProvider.GetRequiredService<ISetupMaintenanceService>()
                .ExportAsync(new ExportSetupProfileRequest(
                    2,
                    new ActorId("local-admin"),
                    new CorrelationId("maintenance-denied-stale"),
                    "wrong-credential".AsMemory()), CancellationToken.None);
            Assert.False(deniedWithStaleVersion.IsSuccess);
            Assert.Equal(FailureCode.PolicyDenied, deniedWithStaleVersion.Failure?.Code);

            var stale = await deniedScope.ServiceProvider.GetRequiredService<ISetupMaintenanceService>()
                .ExportAsync(new ExportSetupProfileRequest(
                    2,
                    new ActorId("local-admin"),
                    new CorrelationId("maintenance-stale"),
                    credential.Value), CancellationToken.None);
            Assert.False(stale.IsSuccess);
            Assert.Equal(FailureCode.ConcurrencyConflict, stale.Failure?.Code);
        }

        await using (var enterScope = _services.CreateAsyncScope())
        {
            var rejectedReason = await enterScope.ServiceProvider.GetRequiredService<ISetupMaintenanceService>()
                .EnterRecoveryAsync(new EnterRecoveryRequest(
                    3,
                    "sk-" + new string('r', 32),
                    new ActorId("local-admin"),
                    new CorrelationId("maintenance-secret-reason"),
                    credential.Value), CancellationToken.None);
            Assert.False(rejectedReason.IsSuccess);
            Assert.Equal(FailureCode.ValidationFailure, rejectedReason.Failure?.Code);

            var entered = await enterScope.ServiceProvider.GetRequiredService<ISetupMaintenanceService>()
                .EnterRecoveryAsync(new EnterRecoveryRequest(
                    3,
                    "provider maintenance",
                    new ActorId("local-admin"),
                    new CorrelationId("maintenance-enter"),
                    credential.Value), CancellationToken.None);
            Assert.True(entered.IsSuccess);
            Assert.Equal(InstallationState.RecoveryRequired, entered.Value.Installation.State);
            Assert.Equal(4, entered.Value.Installation.Version);
            Assert.NotNull(entered.Value.RollbackSnapshot);
            Assert.Equal(3, (await enterScope.ServiceProvider.GetRequiredService<ISetupProfileSnapshotRepository>()
                .ListAsync(installationId, CancellationToken.None)).Count);
        }

        await using (var recoveryDoctorScope = _services.CreateAsyncScope())
        {
            var doctor = await recoveryDoctorScope.ServiceProvider.GetRequiredService<ISetupMaintenanceService>()
                .DoctorAsync(new DoctorRequest(
                    new ActorId("local-admin"),
                    new CorrelationId("maintenance-recovery-doctor")), CancellationToken.None);
            Assert.True(doctor.IsSuccess);
            Assert.False(doctor.Value.IsHealthy);
            Assert.Contains(doctor.Value.Checks, check =>
                check.CheckId == "installation.state" && check.Status == DoctorCheckStatus.Fail);
        }

        await using (var resumeScope = _services.CreateAsyncScope())
        {
            var resumed = await resumeScope.ServiceProvider.GetRequiredService<ISetupMaintenanceService>()
                .ResumeRecoveryAsync(new ResumeRecoveryRequest(
                    4,
                    new ActorId("local-admin"),
                    new CorrelationId("maintenance-resume"),
                    credential.Value), CancellationToken.None);
            Assert.True(resumed.IsSuccess);
            Assert.Equal(InstallationState.Configuring, resumed.Value.Installation.State);
            Assert.Equal(5, resumed.Value.Installation.Version);
        }

        await using (var agentEditScope = _services.CreateAsyncScope())
        {
            var provider = await agentEditScope.ServiceProvider.GetRequiredService<IProviderProfileRepository>()
                .FindByNameAsync(installationId, "primary", CancellationToken.None);
            var agent = await agentEditScope.ServiceProvider.GetRequiredService<IAgentIdentityRepository>()
                .FindByNameAsync(installationId, "Architect", CancellationToken.None);
            Assert.NotNull(provider);
            Assert.NotNull(agent);
            var candidate = CreateAgentCandidate(provider.Id) with
            {
                Mission = "Design, edit, and verify bounded systems.",
            };
            var editor = agentEditScope.ServiceProvider.GetRequiredService<ISetupProfileEditor>();
            var preview = await editor.PreviewAgentAsync(new PreviewAgentEditRequest(
                agent.Id,
                5,
                0,
                candidate,
                new ActorId("local-admin"),
                new CorrelationId("maintenance-agent-preview"),
                credential.Value), CancellationToken.None);
            Assert.True(preview.IsSuccess, preview.Failure?.Message);
            Assert.Equal(64, preview.Value.RequestHash.Length);
            Assert.Single(preview.Value.Changes, change => change.Path == "agent.mission");

            var wrongHash = await editor.ApplyAgentAsync(new ApplyAgentEditRequest(
                agent.Id,
                5,
                0,
                candidate,
                new string('0', 64),
                new ActorId("local-admin"),
                new CorrelationId("maintenance-agent-wrong-hash"),
                credential.Value), CancellationToken.None);
            Assert.False(wrongHash.IsSuccess);
            Assert.Equal(FailureCode.PolicyDenied, wrongHash.Failure?.Code);

            var applied = await editor.ApplyAgentAsync(new ApplyAgentEditRequest(
                agent.Id,
                5,
                0,
                candidate,
                preview.Value.RequestHash,
                new ActorId("local-admin"),
                new CorrelationId("maintenance-agent-preview"),
                credential.Value), CancellationToken.None);
            Assert.True(applied.IsSuccess, applied.Failure?.Message);
            Assert.Equal(6, applied.Value.Installation.Version);
            Assert.Equal(1, applied.Value.Agent.Version);

            var stale = await editor.ApplyAgentAsync(new ApplyAgentEditRequest(
                agent.Id,
                5,
                0,
                candidate,
                preview.Value.RequestHash,
                new ActorId("local-admin"),
                new CorrelationId("maintenance-agent-preview"),
                credential.Value), CancellationToken.None);
            Assert.False(stale.IsSuccess);
            Assert.Equal(FailureCode.ConcurrencyConflict, stale.Failure?.Code);
        }

        await using (var providerEditScope = _services.CreateAsyncScope())
        {
            var provider = await providerEditScope.ServiceProvider.GetRequiredService<IProviderProfileRepository>()
                .FindByNameAsync(installationId, "primary", CancellationToken.None);
            Assert.NotNull(provider);
            var candidate = new ProviderProfileCandidate(
                provider.Name,
                provider.ProviderType,
                provider.Endpoint,
                "deterministic-text-v2",
                provider.SecretReference);
            var editor = providerEditScope.ServiceProvider.GetRequiredService<ISetupProfileEditor>();
            var preview = await editor.PreviewProviderAsync(new PreviewProviderEditRequest(
                provider.Id,
                6,
                0,
                candidate,
                new ActorId("local-admin"),
                new CorrelationId("maintenance-provider-preview"),
                credential.Value), CancellationToken.None);
            Assert.True(preview.IsSuccess, preview.Failure?.Message);
            Assert.Single(preview.Value.Changes, change => change.Path == "provider.model");

            var applied = await editor.ApplyProviderAsync(new ApplyProviderEditRequest(
                provider.Id,
                6,
                0,
                candidate,
                preview.Value.RequestHash,
                new ActorId("local-admin"),
                new CorrelationId("maintenance-provider-preview"),
                credential.Value), CancellationToken.None);
            Assert.True(applied.IsSuccess, applied.Failure?.Message);
            Assert.Equal(7, applied.Value.Installation.Version);
            Assert.Equal(1, applied.Value.Provider.Version);
            Assert.Equal("deterministic-text-v2", applied.Value.Provider.Model);
        }

        await using (var restoreScope = _services.CreateAsyncScope())
        {
            var rollback = (await restoreScope.ServiceProvider.GetRequiredService<ISetupProfileSnapshotRepository>()
                .ListAsync(installationId, CancellationToken.None))
                .First(item => item.Kind is SetupProfileSnapshotKind.Rollback && item.ProfileVersion == 3);
            var restorer = restoreScope.ServiceProvider.GetRequiredService<ISetupProfileRestorer>();
            var preview = await restorer.PreviewAsync(new PreviewSetupProfileRestoreRequest(
                rollback.Id,
                7,
                new ActorId("local-admin"),
                new CorrelationId("maintenance-restore"),
                credential.Value), CancellationToken.None);
            Assert.True(preview.IsSuccess, preview.Failure?.Message);
            Assert.Equal(2, preview.Value.Changes.Count);

            var wrongHash = await restorer.ApplyAsync(new ApplySetupProfileRestoreRequest(
                rollback.Id,
                7,
                new string('0', 64),
                new ActorId("local-admin"),
                new CorrelationId("maintenance-restore"),
                credential.Value), CancellationToken.None);
            Assert.False(wrongHash.IsSuccess);
            Assert.Equal(FailureCode.PolicyDenied, wrongHash.Failure?.Code);

            var restored = await restorer.ApplyAsync(new ApplySetupProfileRestoreRequest(
                rollback.Id,
                7,
                preview.Value.RequestHash,
                new ActorId("local-admin"),
                new CorrelationId("maintenance-restore"),
                credential.Value), CancellationToken.None);
            Assert.True(restored.IsSuccess, restored.Failure?.Message);
            Assert.Equal(8, restored.Value.Installation.Version);
            Assert.Equal(1, restored.Value.RestoredProviderCount);
            Assert.Equal(1, restored.Value.RestoredAgentCount);

            var provider = await restoreScope.ServiceProvider.GetRequiredService<IProviderProfileRepository>()
                .FindByNameAsync(installationId, "primary", CancellationToken.None);
            var agent = await restoreScope.ServiceProvider.GetRequiredService<IAgentIdentityRepository>()
                .FindByNameAsync(installationId, "Architect", CancellationToken.None);
            Assert.NotNull(provider);
            Assert.NotNull(agent);
            Assert.Equal("deterministic-text-v1", provider.Model);
            Assert.Equal("Design bounded and verifiable systems.", agent.Mission);
            Assert.Equal(2, provider.Version);
            Assert.Equal(2, agent.Version);

            var noOpPreview = await restorer.PreviewAsync(new PreviewSetupProfileRestoreRequest(
                rollback.Id,
                8,
                new ActorId("local-admin"),
                new CorrelationId("maintenance-restore-no-op"),
                credential.Value), CancellationToken.None);
            Assert.True(noOpPreview.IsSuccess, noOpPreview.Failure?.Message);
            Assert.Empty(noOpPreview.Value.Changes);
            var noOpApply = await restorer.ApplyAsync(new ApplySetupProfileRestoreRequest(
                rollback.Id,
                8,
                noOpPreview.Value.RequestHash,
                new ActorId("local-admin"),
                new CorrelationId("maintenance-restore-no-op"),
                credential.Value), CancellationToken.None);
            Assert.False(noOpApply.IsSuccess);
            Assert.Equal(FailureCode.ValidationFailure, noOpApply.Failure?.Code);

            var rollbackHash = rollback.Artifact.ContentHash["sha256:".Length..];
            var rollbackPath = Path.Combine(_directory, "artifacts", "sha256", rollbackHash[..2], rollbackHash);
            var rollbackBytes = await File.ReadAllBytesAsync(rollbackPath, CancellationToken.None);
            rollbackBytes[0] ^= 0x01;
            await File.WriteAllBytesAsync(rollbackPath, rollbackBytes, CancellationToken.None);
            var tampered = await restorer.PreviewAsync(new PreviewSetupProfileRestoreRequest(
                rollback.Id,
                8,
                new ActorId("local-admin"),
                new CorrelationId("maintenance-restore-tampered"),
                credential.Value), CancellationToken.None);
            Assert.False(tampered.IsSuccess);
            Assert.Equal(FailureCode.PolicyDenied, tampered.Failure?.Code);
        }

        await using (var recompleteScope = _services.CreateAsyncScope())
        {
            var recompleted = await recompleteScope.ServiceProvider.GetRequiredService<ISetupApplicationService>()
                .CompleteAsync(new CompleteSetupRequest(
                    new ActorId("local-admin"),
                    new CorrelationId("maintenance-recomplete"),
                    credential.Value), CancellationToken.None);
            Assert.True(recompleted.IsSuccess);
            Assert.Equal(InstallationState.Ready, recompleted.Value.Installation.State);
            Assert.Equal(10, recompleted.Value.Installation.Version);
            Assert.Equal(administrator.Id, recompleted.Value.Administrator.Id);
            Assert.Equal(administrator.ClientCredentialReference, recompleted.Value.Administrator.ClientCredentialReference);
        }

        await using var restartScope = _services.CreateAsyncScope();
        Assert.Equal(
            InstallationState.Ready,
            (await restartScope.ServiceProvider.GetRequiredService<IInstallationRepository>()
                .ReadAsync(CancellationToken.None)).State);
        Assert.Equal(11, (await restartScope.ServiceProvider.GetRequiredService<IAuditReader>()
            .ReadAsync(installationId, 0, 20, CancellationToken.None)).Count);
    }

    [Fact]
    public async Task Credential_shaped_identifiers_are_rejected_before_durable_setup_state()
    {
        await InitializeAsync();
        await using var scope = _services.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<ISetupApplicationService>()
            .BeginAsync(new BeginSetupRequest(
                InstallationId.New(),
                new ActorId("sk-" + new string('s', 32)),
                new CorrelationId("credential-identifier")), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(FailureCode.ValidationFailure, result.Failure?.Code);
        Assert.Equal(Guid.Empty, (await scope.ServiceProvider.GetRequiredService<IInstallationRepository>()
            .ReadAsync(CancellationToken.None)).Id.Value);
    }

    [Fact]
    public async Task Environment_inventory_is_redacted_content_addressed_and_audited_atomically()
    {
        const string credentialShapedEvidence = "sk-" + "1234567890abcdefghijklmnop";
        await using var services = BuildServices(
            _directory,
            "environment-redaction.db",
            collection => collection.AddScoped<IEnvironmentProfiler>(_ =>
                new StubEnvironmentProfiler(CreateEnvironmentProfile(credentialShapedEvidence))));
        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>()
            .InitializeAsync(CancellationToken.None);

        var result = await scope.ServiceProvider.GetRequiredService<IEnvironmentInventoryService>()
            .CaptureAsync(
                new CaptureEnvironmentRequest(
                    new ActorId("environment-operator"),
                    new CorrelationId("environment-redaction")),
                CancellationToken.None);

        Assert.True(result.IsSuccess, result.Failure?.Message);
        Assert.Equal(1, result.Value.RedactionCount);
        Assert.StartsWith("sha256:", result.Value.Artifact.ContentHash, StringComparison.Ordinal);
        await using var artifact = await scope.ServiceProvider.GetRequiredService<IArtifactStore>()
            .OpenReadAsync(result.Value.Artifact, CancellationToken.None);
        using var reader = new StreamReader(artifact);
        var json = await reader.ReadToEndAsync(CancellationToken.None);
        Assert.DoesNotContain(credentialShapedEvidence, json, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", json, StringComparison.Ordinal);

        var audit = Assert.Single(await scope.ServiceProvider.GetRequiredService<IAuditReader>()
            .ReadAsync(null, 0, 10, CancellationToken.None));
        Assert.Equal("environment.profile-captured", audit.OperationType);
        Assert.Contains(result.Value.Artifact.ContentHash, audit.Output.Json, StringComparison.Ordinal);
        var integrity = await scope.ServiceProvider.GetRequiredService<IAuditIntegrityVerifier>()
            .VerifyAsync(CancellationToken.None);
        Assert.True(integrity.IsValid);
    }

    public void Dispose()
    {
        _services.Dispose();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private async Task InitializeAsync()
    {
        await using var scope = _services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>().InitializeAsync(CancellationToken.None);
    }

    private static AuditEventDraft CreateAudit(InstallationId installationId, string operation, string correlation) => new(
        Guid.NewGuid(),
        Now,
        installationId,
        new ActorId("operator"),
        new CorrelationId(correlation),
        null,
        operation,
        AuditOutcome.Succeeded,
        new RedactedData("{\"input\":\"redacted\"}"),
        RedactedData.Empty,
        null);

    private static InstallationSnapshot Transition(InstallationSnapshot snapshot, string correlation)
    {
        var result = InstallationStateMachine.Transition(
            snapshot,
            InstallationTrigger.BeginConfiguration,
            Now.AddMinutes(1),
            new ActorId("operator"),
            new CorrelationId(correlation));
        Assert.True(result.IsSuccess);
        return result.Value;
    }

    private static ProviderProfile CreateProviderProfile(
        InstallationId installationId,
        ProviderProfileId profileId,
        string name) => new(
        profileId,
        installationId,
        name,
        "deterministic",
        new Uri("http://127.0.0.1:9000/v1"),
        "deterministic-text-v1",
        new SecretReference(DeterministicSecretStore.Name, Guid.NewGuid().ToString("D")),
        new ProviderCapabilitySummary(true, true, true, false, "fixture"),
        0,
        Now,
        Now,
        new ActorId("operator"),
        new CorrelationId("provider-race"));

    private static AgentIdentityCandidate CreateAgentCandidate(ProviderProfileId providerId) => new(
        "Architect",
        "C# systems architecture",
        "Design bounded and verifiable systems.",
        "en",
        "UTC",
        "Concise",
        null,
        new AgentModelPolicy(providerId, ModelDataLocality.LocalOnly, AllowFallback: false),
        new AgentMemoryPolicy(AgentMemoryScope.Agent, 30),
        new AgentCapabilityPolicy(NetworkPosture.LoopbackOnly, ["tool:repo.read"], ["skill:csharp.review"]),
        new AgentBudget(64, 32, 16_000, 4_000, 3600),
        new ChildAgentLimits(2, 4, 2, 10_000),
        new AgentLearningPolicy(LearningMode.Propose, MutableSkillScope.ProposalWorkspaceOnly));

    private static EnvironmentProfile CreateEnvironmentProfile(string executableName) => new(
        1,
        Now,
        new ActorId("environment-operator"),
        new CorrelationId("environment-redaction"),
        new OperatingSystemProfile(
            HostOperatingSystem.Linux,
            "Ubuntu fixture",
            "6.8.0",
            HostArchitecture.X64,
            HostArchitecture.X64,
            new DistributionProfile("ubuntu", "debian", "24.04", "noble", "Ubuntu 24.04", false)),
        ".NET 10",
        8,
        new WslProfile(false, null, null, "fixture"),
        new IsolationProfile(HostIsolationKind.PhysicalOrUnclassified, "fixture", null),
        new FileSystemProfile("/", "/tmp", '/', true, "ext4", "fixture"),
        new PrivilegeProfile(HostPrivilegeLevel.Standard, "fixture"),
        [],
        [],
        [new ExecutableDescriptor(
            executableName,
            "/opt/agentforge/provider",
            128,
            Now,
            false,
            null,
            "fixture",
            ExecutableTrust.Unknown)],
        false,
        "sha256:" + new string('a', 64));

    private sealed class StubEnvironmentProfiler(EnvironmentProfile profile) : IEnvironmentProfiler
    {
        public Task<DomainResult<EnvironmentProfile>> CaptureAsync(
            CaptureEnvironmentRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(DomainResult.Success(profile));
        }
    }
}
