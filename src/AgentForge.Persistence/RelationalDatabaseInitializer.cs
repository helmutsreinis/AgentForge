using System.Data;
using Microsoft.EntityFrameworkCore;

namespace AgentForge.Persistence;

internal sealed class RelationalDatabaseInitializer(AgentForgeDbContext dbContext) : IDatabaseInitializer
{
    private const long PostgreSqlBootstrapLock = 0x4147454E54464F52;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (dbContext.Database.IsNpgsql())
        {
            await InitializePostgreSqlAsync(cancellationToken);
            return;
        }
        await dbContext.Database.MigrateAsync(cancellationToken);
        await dbContext.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await ExecuteAsync("PRAGMA journal_mode=WAL;", cancellationToken);
            await ExecuteAsync("PRAGMA foreign_keys=ON;", cancellationToken);
            await ExecuteAsync("PRAGMA secure_delete=ON;", cancellationToken);
            await ExecuteAsync("PRAGMA busy_timeout=5000;", cancellationToken);
        }
        finally
        {
            await dbContext.Database.CloseConnectionAsync();
        }
    }

    private async Task InitializePostgreSqlAsync(CancellationToken cancellationToken)
    {
        await dbContext.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await ExecuteAsync($"SELECT pg_advisory_lock({PostgreSqlBootstrapLock});", cancellationToken);
            await dbContext.Database.EnsureCreatedAsync(cancellationToken);
            await ExecuteAsync("""
                CREATE TABLE IF NOT EXISTS agentforge_schema_versions (
                    version character varying(64) PRIMARY KEY,
                    applied_at_utc timestamp with time zone NOT NULL
                );
                INSERT INTO agentforge_schema_versions(version, applied_at_utc)
                VALUES ('r1-20260812', CURRENT_TIMESTAMP)
                ON CONFLICT (version) DO NOTHING;
                """, cancellationToken);
        }
        finally
        {
            try
            {
                await ExecuteAsync($"SELECT pg_advisory_unlock({PostgreSqlBootstrapLock});", cancellationToken);
            }
            finally
            {
                await dbContext.Database.CloseConnectionAsync();
            }
        }
    }

    private async Task ExecuteAsync(string commandText, CancellationToken cancellationToken)
    {
        await using var command = dbContext.Database.GetDbConnection().CreateCommand();
        command.CommandText = commandText;
        command.CommandType = CommandType.Text;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
