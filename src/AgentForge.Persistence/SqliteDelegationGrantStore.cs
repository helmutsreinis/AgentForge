using System.Text.Json;
using AgentForge.Abstractions.Orchestration;
using AgentForge.Domain.Orchestration;
using AgentForge.Domain.Primitives;
using AgentForge.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace AgentForge.Persistence;

internal sealed class SqliteDelegationGrantStore(AgentForgeDbContext dbContext) : IDelegationGrantStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public async ValueTask AddAsync(
        ChildDelegationGrant grant,
        ActorId actorId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(grant);
        if (!DelegationAuthorityEvaluator.IsConsistent(grant))
        {
            throw new ArgumentException("Only a self-consistent delegation grant can be persisted.", nameof(grant));
        }

        await dbContext.DelegationGrants.AddAsync(new DelegationGrantEntity
        {
            Id = grant.Id.Value,
            ParentTaskId = grant.ParentTaskId.Value,
            InstallationId = grant.InstallationId.Value,
            ParentAgentId = grant.ParentAgentId.Value,
            ChildAgentId = grant.ChildAgentId.Value,
            GrantHash = grant.GrantHash,
            GrantJson = JsonSerializer.Serialize(grant, SerializerOptions),
            ActorId = actorId.Value,
            IssuedAtUtcTicks = grant.IssuedAt.UtcTicks,
            ExpiresAtUtcTicks = grant.ExpiresAt.UtcTicks,
            CorrelationId = grant.CorrelationId.Value,
            CausationId = grant.CausationId?.Value,
        }, cancellationToken);
    }

    public async ValueTask<ChildDelegationGrant?> FindAsync(
        ChildDelegationId delegationId,
        CancellationToken cancellationToken)
    {
        var entity = await dbContext.DelegationGrants.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == delegationId.Value, cancellationToken);
        if (entity is null)
        {
            return null;
        }

        var grant = JsonSerializer.Deserialize<ChildDelegationGrant>(entity.GrantJson, SerializerOptions)
            ?? throw new InvalidOperationException("The persisted delegation grant was empty.");
        if (grant.Id.Value != entity.Id || grant.ParentTaskId.Value != entity.ParentTaskId ||
            grant.InstallationId.Value != entity.InstallationId || grant.ParentAgentId.Value != entity.ParentAgentId ||
            grant.ChildAgentId.Value != entity.ChildAgentId ||
            !string.Equals(grant.GrantHash, entity.GrantHash, StringComparison.Ordinal) ||
            grant.IssuedAt.UtcTicks != entity.IssuedAtUtcTicks || grant.ExpiresAt.UtcTicks != entity.ExpiresAtUtcTicks ||
            !string.Equals(grant.CorrelationId.Value, entity.CorrelationId, StringComparison.Ordinal) ||
            !string.Equals(grant.CausationId?.Value, entity.CausationId, StringComparison.Ordinal) ||
            !DelegationAuthorityEvaluator.IsConsistent(grant))
        {
            throw new InvalidOperationException("The persisted delegation grant failed integrity validation.");
        }

        return grant;
    }
}
