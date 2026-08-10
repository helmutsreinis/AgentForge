using System.Text.Json;
using AgentForge.Abstractions.Agents;
using AgentForge.Domain.Agents;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Providers;
using AgentForge.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace AgentForge.Persistence;

internal sealed class SqliteAgentIdentityRepository(AgentForgeDbContext dbContext) : IAgentIdentityRepository
{
    public async ValueTask AddAsync(AgentIdentity agent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(agent);
        await dbContext.AgentIdentities.AddAsync(Map(agent), cancellationToken);
    }

    public async ValueTask<AgentIdentity?> FindByNameAsync(
        InstallationId installationId,
        string name,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var entity = await dbContext.AgentIdentities
            .AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.InstallationId == installationId.Value && item.Name == name,
                cancellationToken);
        return entity is null ? null : Map(entity);
    }

    public async ValueTask<AgentIdentity?> FindByIdAsync(
        AgentIdentityId agentId,
        CancellationToken cancellationToken)
    {
        var entity = await dbContext.AgentIdentities
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == agentId.Value, cancellationToken);
        return entity is null ? null : Map(entity);
    }

    public async Task<IReadOnlyList<AgentIdentity>> ListAsync(
        InstallationId installationId,
        CancellationToken cancellationToken)
    {
        var entities = await dbContext.AgentIdentities
            .AsNoTracking()
            .Where(item => item.InstallationId == installationId.Value)
            .OrderBy(item => item.Name)
            .ToListAsync(cancellationToken);
        return entities.Select(Map).ToArray();
    }

    private static AgentIdentityEntity Map(AgentIdentity agent) => new()
    {
        Id = agent.Id.Value,
        InstallationId = agent.InstallationId.Value,
        Name = agent.Name,
        Expertise = agent.Expertise,
        Mission = agent.Mission,
        PreferredLanguage = agent.PreferredLanguage,
        TimeZone = agent.TimeZone,
        ResponseStyle = agent.ResponseStyle,
        DefaultWorkspace = agent.DefaultWorkspace,
        PrimaryProviderProfileId = agent.ModelPolicy.PrimaryProviderProfileId.Value,
        DataLocality = agent.ModelPolicy.DataLocality.ToString(),
        AllowFallback = agent.ModelPolicy.AllowFallback,
        MemoryScope = agent.MemoryPolicy.Scope.ToString(),
        MemoryRetentionDays = agent.MemoryPolicy.RetentionDays,
        NetworkPosture = agent.CapabilityPolicy.NetworkPosture.ToString(),
        ToolGrantsJson = JsonSerializer.Serialize(agent.CapabilityPolicy.ToolGrants),
        SkillGrantsJson = JsonSerializer.Serialize(agent.CapabilityPolicy.SkillGrants),
        MaxTurns = agent.Budget.MaxTurns,
        MaxToolInvocations = agent.Budget.MaxToolInvocations,
        MaxInputTokens = agent.Budget.MaxInputTokens,
        MaxOutputTokens = agent.Budget.MaxOutputTokens,
        MaxWallClockSeconds = agent.Budget.MaxWallClockSeconds,
        MaxChildDepth = agent.ChildLimits.MaxDepth,
        MaxChildren = agent.ChildLimits.MaxChildren,
        MaxChildConcurrency = agent.ChildLimits.MaxConcurrency,
        MaxChildTotalTokens = agent.ChildLimits.MaxTotalTokens,
        LearningMode = agent.LearningPolicy.Mode.ToString(),
        MutableSkillScope = agent.LearningPolicy.MutableSkillScope.ToString(),
        Version = agent.Version,
        CreatedAt = agent.CreatedAt,
        UpdatedAt = agent.UpdatedAt,
        ActorId = agent.ActorId.Value,
        CorrelationId = agent.CorrelationId.Value,
    };

    private static AgentIdentity Map(AgentIdentityEntity entity) => new(
        new AgentIdentityId(entity.Id),
        new InstallationId(entity.InstallationId),
        entity.Name,
        entity.Expertise,
        entity.Mission,
        entity.PreferredLanguage,
        entity.TimeZone,
        entity.ResponseStyle,
        entity.DefaultWorkspace,
        new AgentModelPolicy(
            new ProviderProfileId(entity.PrimaryProviderProfileId),
            Enum.Parse<ModelDataLocality>(entity.DataLocality, ignoreCase: false),
            entity.AllowFallback),
        new AgentMemoryPolicy(
            Enum.Parse<AgentMemoryScope>(entity.MemoryScope, ignoreCase: false),
            entity.MemoryRetentionDays),
        new AgentCapabilityPolicy(
            Enum.Parse<NetworkPosture>(entity.NetworkPosture, ignoreCase: false),
            DeserializeGrants(entity.ToolGrantsJson),
            DeserializeGrants(entity.SkillGrantsJson)),
        new AgentBudget(
            entity.MaxTurns,
            entity.MaxToolInvocations,
            entity.MaxInputTokens,
            entity.MaxOutputTokens,
            entity.MaxWallClockSeconds),
        new ChildAgentLimits(
            entity.MaxChildDepth,
            entity.MaxChildren,
            entity.MaxChildConcurrency,
            entity.MaxChildTotalTokens),
        new AgentLearningPolicy(
            Enum.Parse<LearningMode>(entity.LearningMode, ignoreCase: false),
            Enum.Parse<MutableSkillScope>(entity.MutableSkillScope, ignoreCase: false)),
        entity.Version,
        entity.CreatedAt,
        entity.UpdatedAt,
        new ActorId(entity.ActorId),
        new CorrelationId(entity.CorrelationId));

    private static string[] DeserializeGrants(string json) =>
        JsonSerializer.Deserialize<string[]>(json) ?? [];
}
