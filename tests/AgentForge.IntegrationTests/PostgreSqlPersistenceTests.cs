using AgentForge.Abstractions.Artifacts;
using AgentForge.Abstractions.Installations;
using AgentForge.Abstractions.Persistence;
using AgentForge.Domain.Installations;
using AgentForge.Domain.Persistence;
using AgentForge.Domain.Primitives;
using AgentForge.Persistence;
using AgentForge.Setup;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AgentForge.IntegrationTests;

public sealed class PostgreSqlPersistenceTests
{
    [Fact]
    public async Task Sqlite_online_backup_verifies_and_restores_database_and_artifacts()
    {
        var root = Path.Combine(Path.GetTempPath(), $"agentforge-backup-{Guid.NewGuid():N}");
        var source = Path.Combine(root, "source");
        var backup = Path.Combine(root, "backup");
        var restored = Path.Combine(root, "restored");
        var installationId = new InstallationId(Guid.NewGuid());
        AgentForge.Domain.Artifacts.ArtifactReference artifact;
        DatabaseBackupManifest manifest;
        try
        {
            var sourceConfiguration = SqliteConfiguration(source);
            await using (var provider = Build(sourceConfiguration))
            {
                await using var scope = provider.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>()
                    .InitializeAsync(CancellationToken.None);
                var repository = scope.ServiceProvider.GetRequiredService<IInstallationRepository>();
                await repository.AddAsync(InstallationSnapshot.CreateUninitialized(
                    installationId, DateTimeOffset.UtcNow, new ActorId("backup-test"),
                    new CorrelationId("backup-test")), CancellationToken.None);
                await using var content = new MemoryStream("artifact-evidence"u8.ToArray());
                artifact = await scope.ServiceProvider.GetRequiredService<IArtifactStore>()
                    .PutAsync(content, "text/plain", CancellationToken.None);
                Directory.CreateDirectory(Path.Combine(source, "secrets"));
                await File.WriteAllTextAsync(Path.Combine(source, "secrets", "protected.bin"), "encrypted-fixture");
                await File.WriteAllTextAsync(Path.Combine(source, "installation-state.json"), "state-fixture");
                var commit = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>()
                    .CommitAsync(CancellationToken.None);
                Assert.True(commit.Succeeded, commit.Failure?.Message);
                var service = scope.ServiceProvider.GetRequiredService<IDatabaseBackupService>();
                var created = await service.CreateAsync(
                    new CreateDatabaseBackupRequest(backup), CancellationToken.None);
                Assert.True(created.IsSuccess, created.Failure?.Message);
                manifest = created.Value;
                var verified = await service.VerifyAsync(backup, manifest, CancellationToken.None);
                Assert.True(verified.IsSuccess, verified.Failure?.Message);
                var result = await service.RestoreAsync(
                    new RestoreDatabaseBackupRequest(backup, manifest, restored), CancellationToken.None);
                Assert.True(result.IsSuccess, result.Failure?.Message);
            }
            await using (var provider = Build(SqliteConfiguration(restored)))
            {
                await using var scope = provider.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>()
                    .InitializeAsync(CancellationToken.None);
                var installation = await scope.ServiceProvider.GetRequiredService<IInstallationStateReader>()
                    .ReadAsync(CancellationToken.None);
                Assert.Equal(installationId, installation.Id);
                await using var content = await scope.ServiceProvider.GetRequiredService<IArtifactStore>()
                    .OpenReadAsync(artifact, CancellationToken.None);
                using var reader = new StreamReader(content);
                Assert.Equal("artifact-evidence", await reader.ReadToEndAsync(CancellationToken.None));
                Assert.Equal("encrypted-fixture", await File.ReadAllTextAsync(
                    Path.Combine(restored, "secrets", "protected.bin")));
                Assert.Equal("state-fixture", await File.ReadAllTextAsync(
                    Path.Combine(restored, "installation-state.json")));
            }
            await File.AppendAllTextAsync(Path.Combine(backup, manifest.Files[0].RelativePath), "tamper");
            await using (var provider = Build(SqliteConfiguration(source)))
            {
                await using var scope = provider.CreateAsyncScope();
                var verified = await scope.ServiceProvider.GetRequiredService<IDatabaseBackupService>()
                    .VerifyAsync(backup, manifest, CancellationToken.None);
                Assert.False(verified.IsSuccess);
            }
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PostgreSql_backup_protocol_uses_exact_tools_secret_environment_and_explicit_target()
    {
        var root = Path.Combine(Path.GetTempPath(), $"agentforge-pg-tools-{Guid.NewGuid():N}");
        var backup = Path.Combine(root, "backup");
        var restored = Path.Combine(root, "restored");
        const string sourceVariable = "AGENTFORGE_TEST_PG_TOOL_SOURCE";
        const string targetVariable = "AGENTFORGE_TEST_PG_TOOL_TARGET";
        System.Environment.SetEnvironmentVariable(sourceVariable,
            "Host=localhost;Database=source;Username=agentforge;Password=source-secret");
        System.Environment.SetEnvironmentVariable(targetVariable,
            "Host=localhost;Database=target;Username=agentforge;Password=target-secret");
        try
        {
            var executable = FindProcessFixture();
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["AgentForge:Installation:DataDirectory"] = Path.Combine(root, "source"),
                    ["AgentForge:Persistence:Provider"] = "PostgreSql",
                    ["AgentForge:Persistence:PostgreSqlConnectionStringEnvironmentVariable"] = sourceVariable,
                    ["AgentForge:Persistence:PostgreSqlDumpExecutable"] = executable,
                    ["AgentForge:Persistence:PostgreSqlRestoreExecutable"] = executable,
                }).Build();
            await using var provider = Build(configuration);
            await using var scope = provider.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<IDatabaseBackupService>();
            var created = await service.CreateAsync(
                new CreateDatabaseBackupRequest(backup), CancellationToken.None);
            Assert.True(created.IsSuccess, created.Failure?.Message);
            Assert.Equal(DatabaseBackupProvider.PostgreSql, created.Value.Provider);
            var result = await service.RestoreAsync(new RestoreDatabaseBackupRequest(
                backup, created.Value, restored, targetVariable), CancellationToken.None);
            Assert.True(result.IsSuccess, result.Failure?.Message);
        }
        finally
        {
            System.Environment.SetEnvironmentVariable(sourceVariable, null);
            System.Environment.SetEnvironmentVariable(targetVariable, null);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void PostgreSql_provider_builds_complete_schema_without_sqlite_collation()
    {
        const string variable = "AGENTFORGE_TEST_POSTGRESQL_MODEL_CONNECTION";
        System.Environment.SetEnvironmentVariable(variable,
            "Host=127.0.0.1;Port=5432;Database=agentforge_model;Username=agentforge;Password=not-used;Timeout=1");
        try
        {
            var configuration = Configuration(variable, $"agentforge-postgres-model-{Guid.NewGuid():N}");
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddAgentForgeSetup(configuration);
            services.AddAgentForgePersistence(configuration);
            using var provider = services.BuildServiceProvider(validateScopes: true);
            using var scope = provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AgentForgeDbContext>();
            var script = db.Database.GenerateCreateScript();
            Assert.Contains("CREATE TABLE installations", script, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("CREATE TABLE skill_bundles", script, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("CREATE EXTENSION IF NOT EXISTS citext", script, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("NOCASE", script, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("Npgsql.EntityFrameworkCore.PostgreSQL", db.Database.ProviderName);
            Assert.NotNull(scope.ServiceProvider.GetRequiredService<IInstallationRepository>());
        }
        finally
        {
            System.Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [PostgreSqlLiveFact]
    public async Task Live_postgresql_bootstrap_and_repository_restart()
    {
        const string variable = "AGENTFORGE_TEST_POSTGRESQL_CONNECTION";
        var configuration = Configuration(variable, $"agentforge-postgres-live-{Guid.NewGuid():N}");
        var installationId = new InstallationId(Guid.NewGuid());
        await using (var provider = Build(configuration))
        {
            await using var scope = provider.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>().InitializeAsync(CancellationToken.None);
            var repository = scope.ServiceProvider.GetRequiredService<IInstallationRepository>();
            var now = DateTimeOffset.UtcNow;
            var created = InstallationSnapshot.CreateUninitialized(
                installationId, now, new ActorId("postgres-live"), new CorrelationId(Guid.NewGuid().ToString("N")));
            await repository.AddAsync(created, CancellationToken.None);
            var commit = await scope.ServiceProvider.GetRequiredService<AgentForge.Abstractions.Persistence.IUnitOfWork>()
                .CommitAsync(CancellationToken.None);
            Assert.True(commit.Succeeded, commit.Failure?.Message);
        }
        await using (var restarted = Build(configuration))
        {
            await using var scope = restarted.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>().InitializeAsync(CancellationToken.None);
            var loaded = await scope.ServiceProvider.GetRequiredService<IInstallationRepository>()
                .ReadAsync(CancellationToken.None);
            Assert.Equal(installationId, loaded.Id);
        }
    }

    [PostgreSqlBackupLiveFact]
    public async Task Live_postgresql_backup_and_restore_into_explicit_isolated_target()
    {
        var root = Path.Combine(Path.GetTempPath(), $"agentforge-postgres-backup-{Guid.NewGuid():N}");
        var backup = Path.Combine(root, "backup");
        var artifacts = Path.Combine(root, "restored-artifacts");
        try
        {
            var configuration = PostgreSqlBackupConfiguration(
                "AGENTFORGE_TEST_POSTGRESQL_CONNECTION", Path.Combine(root, "source-artifacts"));
            await using (var provider = Build(configuration))
            {
                await using var scope = provider.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>()
                    .InitializeAsync(CancellationToken.None);
                var service = scope.ServiceProvider.GetRequiredService<IDatabaseBackupService>();
                var created = await service.CreateAsync(
                    new CreateDatabaseBackupRequest(backup), CancellationToken.None);
                Assert.True(created.IsSuccess, created.Failure?.Message);
                var restored = await service.RestoreAsync(new RestoreDatabaseBackupRequest(
                    backup, created.Value, artifacts, "AGENTFORGE_TEST_POSTGRESQL_RESTORE_CONNECTION"),
                    CancellationToken.None);
                Assert.True(restored.IsSuccess, restored.Failure?.Message);
            }
            var target = PostgreSqlBackupConfiguration(
                "AGENTFORGE_TEST_POSTGRESQL_RESTORE_CONNECTION", artifacts);
            await using var targetProvider = Build(target);
            await using var targetScope = targetProvider.CreateAsyncScope();
            await targetScope.ServiceProvider.GetRequiredService<IDatabaseInitializer>()
                .InitializeAsync(CancellationToken.None);
            Assert.True(await targetScope.ServiceProvider.GetRequiredService<AgentForgeDbContext>()
                .Database.CanConnectAsync());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static ServiceProvider Build(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAgentForgeSetup(configuration);
        services.AddAgentForgePersistence(configuration);
        return services.BuildServiceProvider(validateScopes: true);
    }

    private static IConfiguration Configuration(string variable, string directory) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AgentForge:Installation:DataDirectory"] = Path.Combine(Path.GetTempPath(), directory),
            ["AgentForge:Persistence:Provider"] = "PostgreSql",
            ["AgentForge:Persistence:PostgreSqlConnectionStringEnvironmentVariable"] = variable,
        }).Build();

    private static IConfiguration SqliteConfiguration(string directory) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AgentForge:Installation:DataDirectory"] = directory,
            ["AgentForge:Persistence:Provider"] = "Sqlite",
            ["AgentForge:Persistence:EnableConnectionPooling"] = "false",
        }).Build();

    private static IConfiguration PostgreSqlBackupConfiguration(string variable, string directory) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AgentForge:Installation:DataDirectory"] = directory,
            ["AgentForge:Persistence:Provider"] = "PostgreSql",
            ["AgentForge:Persistence:PostgreSqlConnectionStringEnvironmentVariable"] = variable,
            ["AgentForge:Persistence:PostgreSqlDumpExecutable"] =
                System.Environment.GetEnvironmentVariable("AGENTFORGE_TEST_PG_DUMP"),
            ["AgentForge:Persistence:PostgreSqlRestoreExecutable"] =
                System.Environment.GetEnvironmentVariable("AGENTFORGE_TEST_PG_RESTORE"),
        }).Build();

    private static string FindProcessFixture()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "AgentForge.slnx"))) root = root.Parent;
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Debug";
        var name = OperatingSystem.IsWindows() ? "AgentForge.ProcessFixture.exe" : "AgentForge.ProcessFixture";
        return Path.Combine(root!.FullName, "tests", "AgentForge.ProcessFixture", "bin", configuration, "net10.0", name);
    }
}

internal sealed class PostgreSqlLiveFactAttribute : FactAttribute
{
    public PostgreSqlLiveFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(System.Environment.GetEnvironmentVariable(
                "AGENTFORGE_TEST_POSTGRESQL_CONNECTION")))
            Skip = "Set AGENTFORGE_TEST_POSTGRESQL_CONNECTION to an isolated database to run this live gate.";
    }
}

internal sealed class PostgreSqlBackupLiveFactAttribute : FactAttribute
{
    private static readonly string[] RequiredVariables =
    [
        "AGENTFORGE_TEST_POSTGRESQL_CONNECTION",
        "AGENTFORGE_TEST_POSTGRESQL_RESTORE_CONNECTION",
        "AGENTFORGE_TEST_PG_DUMP",
        "AGENTFORGE_TEST_PG_RESTORE",
    ];

    public PostgreSqlBackupLiveFactAttribute()
    {
        if (RequiredVariables.Any(name => string.IsNullOrWhiteSpace(
                System.Environment.GetEnvironmentVariable(name))))
            Skip = "Set isolated source/target PostgreSQL connections and exact pg_dump/pg_restore paths.";
    }
}
