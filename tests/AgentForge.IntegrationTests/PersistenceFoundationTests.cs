using System.Text;
using AgentForge.Abstractions.Artifacts;
using AgentForge.Abstractions.Auditing;
using AgentForge.Abstractions.Installations;
using AgentForge.Abstractions.Persistence;
using AgentForge.Audit;
using AgentForge.Domain.Auditing;
using AgentForge.Domain.Installations;
using AgentForge.Domain.Primitives;
using AgentForge.Persistence;
using AgentForge.Security;
using AgentForge.Setup;
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
}
