namespace AgentForge.Persistence.Entities;

internal sealed class MemoryEntryEntity
{
    public required Guid Id { get; init; }

    public required Guid InstallationId { get; init; }

    public required Guid AgentId { get; init; }

    public required string ScopeId { get; init; }

    public required string Kind { get; init; }

    public required string Content { get; init; }

    public required string ContentHash { get; init; }

    public required string SourceKind { get; init; }

    public required string SourceId { get; init; }

    public required string SourceEvidenceHash { get; init; }

    public string? SourceUri { get; init; }

    public long CreatedAtUtcTicks { get; init; }

    public long ExpiresAtUtcTicks { get; init; }

    public long Version { get; init; }

    public required string ActorId { get; init; }

    public required string CorrelationId { get; init; }

    public string? CausationId { get; init; }

    public required string IdempotencyKey { get; init; }

    public int RedactionCount { get; init; }
}
