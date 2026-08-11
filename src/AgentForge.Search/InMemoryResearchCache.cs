using System.Collections.Concurrent;
using AgentForge.Abstractions.Search;
using AgentForge.Domain.Search;

namespace AgentForge.Search;

public sealed class InMemoryResearchCache : IResearchCache
{
    private readonly ConcurrentDictionary<string, ResearchResponse> _entries = new(StringComparer.Ordinal);

    public Task<ResearchResponse?> ReadAsync(
        string queryHash,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_entries.TryGetValue(queryHash, out var response) && response.ExpiresAtUtc > nowUtc)
        {
            return Task.FromResult<ResearchResponse?>(response with { IsCacheHit = true });
        }

        _entries.TryRemove(queryHash, out _);
        return Task.FromResult<ResearchResponse?>(null);
    }

    public Task WriteAsync(ResearchResponse response, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _entries[response.QueryHash] = response with { IsCacheHit = false };
        return Task.CompletedTask;
    }
}
