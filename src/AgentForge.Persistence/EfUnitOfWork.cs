using AgentForge.Abstractions.Persistence;
using AgentForge.Domain.Persistence;
using Microsoft.EntityFrameworkCore;

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
    }
}
