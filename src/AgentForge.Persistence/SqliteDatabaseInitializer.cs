using System.Data;
using Microsoft.EntityFrameworkCore;

namespace AgentForge.Persistence;

internal sealed class SqliteDatabaseInitializer(AgentForgeDbContext dbContext) : IDatabaseInitializer
{
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await dbContext.Database.MigrateAsync(cancellationToken);
        await dbContext.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await ExecutePragmaAsync("PRAGMA journal_mode=WAL;", cancellationToken);
            await ExecutePragmaAsync("PRAGMA foreign_keys=ON;", cancellationToken);
            await ExecutePragmaAsync("PRAGMA secure_delete=ON;", cancellationToken);
            await ExecutePragmaAsync("PRAGMA busy_timeout=5000;", cancellationToken);
        }
        finally
        {
            await dbContext.Database.CloseConnectionAsync();
        }
    }

    private async Task ExecutePragmaAsync(string commandText, CancellationToken cancellationToken)
    {
        await using var command = dbContext.Database.GetDbConnection().CreateCommand();
        command.CommandText = commandText;
        command.CommandType = CommandType.Text;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
