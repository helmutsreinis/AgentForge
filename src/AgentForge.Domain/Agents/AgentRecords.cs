using AgentForge.Domain.Primitives;
using AgentForge.Domain.Providers;

namespace AgentForge.Domain.Agents;

public readonly record struct AgentIdentityId(Guid Value)
{
    public override string ToString() => Value.ToString("D");
}

public enum AgentMemoryScope
{
    Task,
    Agent,
    Operator,
}

public enum ModelDataLocality
{
    LocalOnly,
    CloudAllowed,
}

public enum NetworkPosture
{
    Denied,
    LoopbackOnly,
}

public enum LearningMode
{
    Off,
    Observe,
    Propose,
    ScopedAuto,
}

public enum MutableSkillScope
{
    None,
    ProposalWorkspaceOnly,
    ApprovedSkillClasses,
}

public sealed record AgentModelPolicy(
    ProviderProfileId PrimaryProviderProfileId,
    ModelDataLocality DataLocality,
    bool AllowFallback);

public sealed record AgentMemoryPolicy(
    AgentMemoryScope Scope,
    int RetentionDays);

public sealed record AgentCapabilityPolicy(
    NetworkPosture NetworkPosture,
    IReadOnlyList<string> ToolGrants,
    IReadOnlyList<string> SkillGrants);

public sealed record AgentBudget(
    int MaxTurns,
    int MaxToolInvocations,
    long MaxInputTokens,
    long MaxOutputTokens,
    int MaxWallClockSeconds);

public sealed record ChildAgentLimits(
    int MaxDepth,
    int MaxChildren,
    int MaxConcurrency,
    long MaxTotalTokens);

public sealed record AgentLearningPolicy(
    LearningMode Mode,
    MutableSkillScope MutableSkillScope);

public sealed record AgentIdentityCandidate(
    string Name,
    string? Expertise,
    string? Mission,
    string PreferredLanguage,
    string TimeZone,
    string ResponseStyle,
    string? DefaultWorkspace,
    AgentModelPolicy ModelPolicy,
    AgentMemoryPolicy MemoryPolicy,
    AgentCapabilityPolicy CapabilityPolicy,
    AgentBudget Budget,
    ChildAgentLimits ChildLimits,
    AgentLearningPolicy LearningPolicy);

public sealed record AgentIdentity(
    AgentIdentityId Id,
    InstallationId InstallationId,
    string Name,
    string? Expertise,
    string? Mission,
    string PreferredLanguage,
    string TimeZone,
    string ResponseStyle,
    string? DefaultWorkspace,
    AgentModelPolicy ModelPolicy,
    AgentMemoryPolicy MemoryPolicy,
    AgentCapabilityPolicy CapabilityPolicy,
    AgentBudget Budget,
    ChildAgentLimits ChildLimits,
    AgentLearningPolicy LearningPolicy,
    long Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    ActorId ActorId,
    CorrelationId CorrelationId);

public enum CapabilityDecision
{
    Allow,
    Deny,
    RequireApproval,
}

public sealed record EffectiveCapability(
    string CapabilityId,
    CapabilityDecision Decision,
    string Reason);

public sealed record EffectiveAgentDefinition(
    AgentIdentityCandidate Agent,
    string ProviderName,
    string Model,
    ProviderCapabilitySummary ProviderCapabilities,
    IReadOnlyList<EffectiveCapability> Capabilities);

public sealed record PreviewAgentRequest(
    AgentIdentityCandidate Candidate,
    ActorId ActorId,
    CorrelationId CorrelationId);

public sealed record CreateAgentRequest(
    AgentIdentityCandidate Candidate,
    ActorId ActorId,
    CorrelationId CorrelationId);

public sealed record CreateAgentResult(
    AgentIdentity Agent,
    EffectiveAgentDefinition EffectiveDefinition);
