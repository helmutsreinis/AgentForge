using System.Collections.Immutable;
using AgentForge.Domain.Agents;
using AgentForge.Domain.Primitives;

namespace AgentForge.Domain.Memory;

public readonly record struct MemoryEntryId(Guid Value)
{
    public override string ToString() => Value.ToString("D");
}

public enum MemoryKind
{
    Working,
    Task,
    Episodic,
    Semantic,
    User,
    Environment,
    Procedural,
}

public enum MemorySourceKind
{
    UserInput,
    UserCorrection,
    TaskEvidence,
    Trajectory,
    SearchCitation,
    EnvironmentProfile,
    SkillReceipt,
}

public sealed record MemorySource(
    MemorySourceKind Kind,
    string SourceId,
    string EvidenceHash,
    Uri? SourceUri);

public sealed record MemoryEntry(
    MemoryEntryId Id,
    InstallationId InstallationId,
    AgentIdentityId AgentId,
    string ScopeId,
    MemoryKind Kind,
    string Content,
    string ContentHash,
    MemorySource Source,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    long Version,
    ActorId ActorId,
    CorrelationId CorrelationId,
    CorrelationId? CausationId,
    string IdempotencyKey,
    int RedactionCount);

public sealed record CreateMemoryRequest(
    MemoryEntryId Id,
    InstallationId InstallationId,
    AgentIdentityId AgentId,
    string ScopeId,
    MemoryKind Kind,
    string Content,
    MemorySource Source,
    DateTimeOffset ExpiresAtUtc,
    ActorId ActorId,
    CorrelationId CorrelationId,
    CorrelationId? CausationId,
    string IdempotencyKey);

public sealed record MemoryQuery(
    InstallationId InstallationId,
    AgentIdentityId AgentId,
    string ScopeId,
    string Text,
    ImmutableArray<MemoryKind> Kinds,
    int MaximumResults,
    DateTimeOffset AsOfUtc);

public sealed record DeleteMemoryRequest(
    MemoryEntryId Id,
    InstallationId InstallationId,
    AgentIdentityId AgentId,
    string ScopeId,
    ActorId ActorId,
    CorrelationId CorrelationId,
    CorrelationId? CausationId);
