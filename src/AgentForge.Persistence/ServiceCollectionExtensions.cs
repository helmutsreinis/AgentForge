using AgentForge.Abstractions.Agents;
using AgentForge.Abstractions.Artifacts;
using AgentForge.Abstractions.Auditing;
using AgentForge.Abstractions.Channels;
using AgentForge.Abstractions.Coding;
using AgentForge.Abstractions.Devices;
using AgentForge.Abstractions.Installations;
using AgentForge.Abstractions.Learning;
using AgentForge.Abstractions.Memory;
using AgentForge.Abstractions.Models;
using AgentForge.Abstractions.Orchestration;
using AgentForge.Abstractions.Persistence;
using AgentForge.Abstractions.Providers;
using AgentForge.Abstractions.Runtime;
using AgentForge.Abstractions.Scheduling;
using AgentForge.Abstractions.Security;
using AgentForge.Abstractions.Setup;
using AgentForge.Abstractions.Skills;
using AgentForge.Abstractions.Time;
using AgentForge.Abstractions.Tools;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AgentForge.Persistence;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAgentForgePersistence(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<PersistenceOptions>()
            .Bind(configuration.GetSection(PersistenceOptions.SectionName))
            .Validate(options => IsFileName(options.DatabaseFileName), "DatabaseFileName must be a simple file name")
            .Validate(options => IsDirectoryName(options.ArtifactDirectoryName), "ArtifactDirectoryName must be a relative directory name")
            .Validate(options => Enum.IsDefined(options.Provider), "Provider must be Sqlite or PostgreSql")
            .Validate(options => IsEnvironmentVariableName(options.PostgreSqlConnectionStringEnvironmentVariable),
                "PostgreSqlConnectionStringEnvironmentVariable must be a simple environment variable name")
            .Validate(options => IsEmptyOrFullyQualifiedPath(options.PostgreSqlDumpExecutable) &&
                IsEmptyOrFullyQualifiedPath(options.PostgreSqlRestoreExecutable),
                "PostgreSQL backup tool paths must be empty or fully qualified")
            .ValidateOnStart();

        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IIdentifierGenerator>(provider => (SystemClock)provider.GetRequiredService<IClock>());
        services.AddDbContext<AgentForgeDbContext>((serviceProvider, dbOptions) =>
        {
            var dataDirectory = serviceProvider.GetRequiredService<IDataDirectoryProvider>().GetDataDirectory();
            Directory.CreateDirectory(dataDirectory);
            var persistence = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<PersistenceOptions>>().Value;
            if (persistence.Provider == PersistenceProvider.PostgreSql)
            {
                var connectionString = System.Environment.GetEnvironmentVariable(
                    persistence.PostgreSqlConnectionStringEnvironmentVariable);
                if (string.IsNullOrWhiteSpace(connectionString))
                    throw new InvalidOperationException(
                        "The configured PostgreSQL connection-string environment variable is unavailable.");
                dbOptions.UseNpgsql(connectionString, npgsql => npgsql.EnableRetryOnFailure(3));
            }
            else
            {
                var databasePath = Path.Combine(dataDirectory, persistence.DatabaseFileName);
                var pooling = persistence.EnableConnectionPooling ? "True" : "False";
                dbOptions.UseSqlite($"Data Source={databasePath};Cache=Shared;Pooling={pooling}");
            }
            dbOptions.EnableDetailedErrors(false).EnableSensitiveDataLogging(false);
        });

        services.AddScoped<IDatabaseInitializer, RelationalDatabaseInitializer>();
        services.AddScoped<IInstallationRepository, SqliteInstallationRepository>();
        services.AddScoped<IInstallationStateReader>(provider => provider.GetRequiredService<IInstallationRepository>());
        services.AddScoped<IUnitOfWork, EfUnitOfWork>();
        services.AddScoped<IEventOutbox, EventOutbox>();
        services.AddScoped<IDatabaseBackupService, DatabaseBackupService>();
        services.AddScoped<SqliteAuditJournal>();
        services.AddScoped<IAuditSink>(provider => provider.GetRequiredService<SqliteAuditJournal>());
        services.AddScoped<IAuditReader>(provider => provider.GetRequiredService<SqliteAuditJournal>());
        services.AddScoped<IArtifactStore, FileSystemArtifactStore>();
        services.AddScoped<IProviderProfileRepository, SqliteProviderProfileRepository>();
        services.AddScoped<IAgentIdentityRepository, SqliteAgentIdentityRepository>();
        services.AddScoped<IModelRouteAuthoritySnapshotReader, SqliteModelRouteAuthoritySnapshotReader>();
        services.AddScoped<IModelRunRepository, SqliteModelRunRepository>();
        services.AddScoped<IModelBudgetLedgerRepository, SqliteModelBudgetLedgerRepository>();
        services.AddScoped<IRunSnapshotStore, SqliteRunSnapshotStore>();
        services.AddScoped<ITaskSnapshotStore, SqliteTaskSnapshotStore>();
        services.AddScoped<IRunConversationRepository, SqliteRunConversationRepository>();
        services.AddScoped<IDelegationGrantStore, SqliteDelegationGrantStore>();
        services.AddScoped<IScheduleSnapshotStore, SqliteScheduleSnapshotStore>();
        services.AddScoped<IScheduledAgentRunStore, SqliteScheduledAgentRunStore>();
        services.AddScoped<ISkillRegistryRepository, SqliteSkillRegistryRepository>();
        services.AddScoped<ISkillProposalRepository, SqliteSkillProposalRepository>();
        services.AddScoped<ISkillRunSnapshotStore, SqliteSkillRunSnapshotStore>();
        services.AddScoped<ICodingSessionRepository, SqliteCodingSessionRepository>();
        services.AddScoped<IMemoryRepository, SqliteMemoryRepository>();
        services.AddScoped<SqliteChannelRepository>();
        services.AddScoped<IChannelRepository>(provider => provider.GetRequiredService<SqliteChannelRepository>());
        services.AddScoped<IChannelIdentityResolver>(provider => provider.GetRequiredService<SqliteChannelRepository>());
        services.AddScoped<IChannelIdentityBindingStore>(provider => provider.GetRequiredService<SqliteChannelRepository>());
        services.AddScoped<ISerialCaptureRepository, SqliteSerialCaptureRepository>();
        services.AddScoped<IDecoderProposalRepository, SqliteDecoderProposalRepository>();
        services.AddScoped<ILearningRepository, SqliteLearningRepository>();
        services.AddScoped<SqliteModelProviderHealthRepository>();
        services.AddScoped<IModelProviderHealthRepository>(provider =>
            provider.GetRequiredService<SqliteModelProviderHealthRepository>());
        services.AddScoped<IModelProviderHealthSource>(provider =>
            provider.GetRequiredService<SqliteModelProviderHealthRepository>());
        services.AddScoped<ILocalAdministratorRepository, SqliteLocalAdministratorRepository>();
        services.AddScoped<ISetupProfileSnapshotRepository, SqliteSetupProfileSnapshotRepository>();
        services.AddScoped<ICapabilityApprovalRepository, SqliteCapabilityApprovalRepository>();
        services.AddScoped<IToolInvocationRepository, SqliteToolInvocationRepository>();
        services.AddScoped<ITrajectoryExportRepository, SqliteTrajectoryExportRepository>();
        return services;
    }

    private static bool IsFileName(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        !Path.IsPathRooted(value) &&
        string.Equals(value, Path.GetFileName(value), StringComparison.Ordinal);

    private static bool IsDirectoryName(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        !Path.IsPathRooted(value) &&
        !value.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Contains("..", StringComparer.Ordinal);

    private static bool IsEnvironmentVariableName(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 128 &&
        (char.IsAsciiLetter(value[0]) || value[0] == '_') &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character == '_');

    private static bool IsEmptyOrFullyQualifiedPath(string value) =>
        string.IsNullOrEmpty(value) || Path.IsPathFullyQualified(value);
}
