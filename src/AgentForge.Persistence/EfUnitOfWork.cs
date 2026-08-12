using AgentForge.Abstractions.Persistence;
using AgentForge.Domain.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AgentForge.Persistence;

internal sealed class EfUnitOfWork(AgentForgeDbContext dbContext) : IUnitOfWork
{
    public async Task<CommitResult> CommitAsync(CancellationToken cancellationToken)
    {
        try
        {
            var affectedRows = await dbContext.SaveChangesAsync(cancellationToken);
            return CommitResult.Success(affectedRows);
        }
        catch (DbUpdateConcurrencyException)
        {
            dbContext.ChangeTracker.Clear();
            return CommitResult.ConcurrencyConflict("Durable state changed after it was read. Reload and retry the operation.");
        }
        catch (DbUpdateException exception) when (exception.InnerException is SqliteException { SqliteErrorCode: 19 })
        {
            dbContext.ChangeTracker.Clear();
            return CommitResult.ConcurrencyConflict("A durable uniqueness or relationship constraint changed. Reload and retry the operation.");
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
        { SqlState: "23503" or "23505" })
        {
            dbContext.ChangeTracker.Clear();
            return CommitResult.ConcurrencyConflict("A durable uniqueness or relationship constraint changed. Reload and retry the operation.");
        }
    }
}
