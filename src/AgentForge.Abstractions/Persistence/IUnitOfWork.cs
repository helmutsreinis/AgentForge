using AgentForge.Domain.Persistence;

namespace AgentForge.Abstractions.Persistence;

public interface IUnitOfWork
{
    Task<CommitResult> CommitAsync(CancellationToken cancellationToken);
}
