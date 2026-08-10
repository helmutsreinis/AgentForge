using System.Text;
using AgentForge.Abstractions.Artifacts;
using AgentForge.Abstractions.Auditing;
using AgentForge.Abstractions.Installations;
using AgentForge.Abstractions.Persistence;
using AgentForge.Abstractions.Providers;
using AgentForge.Abstractions.Security;
using AgentForge.Abstractions.Setup;
using AgentForge.Audit;
using AgentForge.Domain.Auditing;
using AgentForge.Domain.Installations;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Providers;
using AgentForge.Domain.Security;
using AgentForge.Domain.Setup;
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

    private static ServiceProvider BuildServices(string directory, string databaseFileName)
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
}
