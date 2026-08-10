using AgentForge.Abstractions.Agents;
using AgentForge.Abstractions.Artifacts;
using AgentForge.Abstractions.Auditing;
using AgentForge.Abstractions.Installations;
using AgentForge.Abstractions.Persistence;
using AgentForge.Abstractions.Providers;
using AgentForge.Abstractions.Security;
using AgentForge.Abstractions.Time;
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
            .ValidateOnStart();

        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IIdentifierGenerator>(provider => (SystemClock)provider.GetRequiredService<IClock>());
        services.AddDbContext<AgentForgeDbContext>((serviceProvider, dbOptions) =>
        {
            var dataDirectory = serviceProvider.GetRequiredService<IDataDirectoryProvider>().GetDataDirectory();
            Directory.CreateDirectory(dataDirectory);
            var persistence = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<PersistenceOptions>>().Value;
            var databasePath = Path.Combine(dataDirectory, persistence.DatabaseFileName);
            var pooling = persistence.EnableConnectionPooling ? "True" : "False";
            dbOptions.UseSqlite($"Data Source={databasePath};Cache=Shared;Pooling={pooling}");
        });

        services.AddScoped<IDatabaseInitializer, SqliteDatabaseInitializer>();
        services.AddScoped<IInstallationRepository, SqliteInstallationRepository>();
        services.AddScoped<IInstallationStateReader>(provider => provider.GetRequiredService<IInstallationRepository>());
        services.AddScoped<IUnitOfWork, EfUnitOfWork>();
        services.AddScoped<SqliteAuditJournal>();
        services.AddScoped<IAuditSink>(provider => provider.GetRequiredService<SqliteAuditJournal>());
        services.AddScoped<IAuditReader>(provider => provider.GetRequiredService<SqliteAuditJournal>());
        services.AddScoped<IArtifactStore, FileSystemArtifactStore>();
        services.AddScoped<IProviderProfileRepository, SqliteProviderProfileRepository>();
        services.AddScoped<IAgentIdentityRepository, SqliteAgentIdentityRepository>();
        services.AddScoped<ILocalAdministratorRepository, SqliteLocalAdministratorRepository>();
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
}
