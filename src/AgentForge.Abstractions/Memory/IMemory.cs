using AgentForge.Domain.Memory;
using AgentForge.Domain.Primitives;

namespace AgentForge.Abstractions.Memory;

public interface IMemoryRepository
{
    ValueTask<MemoryEntry?> FindByIdAsync(MemoryEntryId id, CancellationToken cancellationToken);

    ValueTask<MemoryEntry?> FindByIdempotencyKeyAsync(
        InstallationId installationId,
        string idempotencyKey,
        CancellationToken cancellationToken);

    ValueTask AddAsync(MemoryEntry entry, CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<MemoryEntry>> SearchAsync(MemoryQuery query, CancellationToken cancellationToken);

    ValueTask DeleteAsync(MemoryEntryId id, CancellationToken cancellationToken);
}

public interface IMemoryService
{
    Task<DomainResult<MemoryEntry>> CreateAsync(CreateMemoryRequest request, CancellationToken cancellationToken);

    Task<DomainResult<IReadOnlyList<MemoryEntry>>> SearchAsync(MemoryQuery query, CancellationToken cancellationToken);

    Task<DomainResult<bool>> DeleteAsync(DeleteMemoryRequest request, CancellationToken cancellationToken);
}
