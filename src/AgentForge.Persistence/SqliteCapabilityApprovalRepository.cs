using AgentForge.Abstractions.Security;
using AgentForge.Domain.Agents;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Security;
using AgentForge.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace AgentForge.Persistence;

internal sealed class SqliteCapabilityApprovalRepository(AgentForgeDbContext dbContext)
    : ICapabilityApprovalRepository
{
    public async ValueTask AddAsync(CapabilityApproval approval, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(approval);
        await dbContext.CapabilityApprovals.AddAsync(Map(approval), cancellationToken);
    }

    public async ValueTask UpdateAsync(
        CapabilityApproval approval,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(approval);
        var entity = Map(approval);
        dbContext.Attach(entity);
        dbContext.Entry(entity).State = EntityState.Modified;
        dbContext.Entry(entity).Property(item => item.Version).OriginalValue = expectedVersion;
        await ValueTask.CompletedTask;
    }

    public async ValueTask<CapabilityApproval?> FindByIdAsync(
        CapabilityApprovalId approvalId,
        CancellationToken cancellationToken)
    {
        var entity = await dbContext.CapabilityApprovals.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == approvalId.Value, cancellationToken);
        return entity is null ? null : Map(entity);
    }

    public async ValueTask<CapabilityApproval?> FindByIdempotencyKeyAsync(
        InstallationId installationId,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        var entity = await dbContext.CapabilityApprovals.AsNoTracking()
            .SingleOrDefaultAsync(item =>
                item.InstallationId == installationId.Value && item.IdempotencyKey == idempotencyKey,
                cancellationToken);
        return entity is null ? null : Map(entity);
    }

    public async ValueTask<CapabilityApproval?> FindLatestAsync(
        InstallationId installationId,
        AgentIdentityId agentId,
        string requestHash,
        CancellationToken cancellationToken)
    {
        var entity = await dbContext.CapabilityApprovals.AsNoTracking()
            .Where(item => item.InstallationId == installationId.Value &&
                item.AgentId == agentId.Value && item.RequestHash == requestHash)
            .OrderByDescending(item => item.CreatedAtUtcTicks)
            .ThenByDescending(item => item.Id)
            .FirstOrDefaultAsync(cancellationToken);
        return entity is null ? null : Map(entity);
    }

    private static CapabilityApprovalEntity Map(CapabilityApproval approval) => new()
    {
        Id = approval.Id.Value,
        InstallationId = approval.InstallationId.Value,
        InstallationVersion = approval.InstallationVersion,
        AgentId = approval.AgentId.Value,
        AgentVersion = approval.AgentVersion,
        RequestActorId = approval.RequestActorId.Value,
        CapabilityId = approval.CapabilityId,
        RiskClass = approval.RiskClass.ToString(),
        ToolId = approval.ToolId,
        ToolVersion = approval.ToolVersion,
        ToolDescriptorHash = approval.ToolDescriptorHash,
        ParametersHash = approval.ParametersHash,
        TargetKind = approval.TargetKind.ToString(),
        TargetHash = approval.TargetHash,
        WorkspaceHash = approval.WorkspaceHash,
        RequestHash = approval.RequestHash,
        Disposition = approval.Disposition.ToString(),
        State = approval.State.ToString(),
        CreatedAtUtcTicks = approval.CreatedAt.UtcTicks,
        ExpiresAtUtcTicks = approval.ExpiresAt.UtcTicks,
        DecidedBy = approval.DecidedBy.Value,
        CorrelationId = approval.CorrelationId.Value,
        PreviewHash = approval.PreviewHash,
        IdempotencyKey = approval.IdempotencyKey,
        Version = approval.Version,
        ConsumedAtUtcTicks = approval.ConsumedAt?.UtcTicks,
        RevokedAtUtcTicks = approval.RevokedAt?.UtcTicks,
    };

    private static CapabilityApproval Map(CapabilityApprovalEntity entity) => new(
        new CapabilityApprovalId(entity.Id),
        new InstallationId(entity.InstallationId),
        entity.InstallationVersion,
        new AgentIdentityId(entity.AgentId),
        entity.AgentVersion,
        new ActorId(entity.RequestActorId),
        entity.CapabilityId,
        Enum.Parse<CapabilityRiskClass>(entity.RiskClass, ignoreCase: false),
        entity.ToolId,
        entity.ToolVersion,
        entity.ToolDescriptorHash,
        entity.ParametersHash,
        Enum.Parse<AuthorizationTargetKind>(entity.TargetKind, ignoreCase: false),
        entity.TargetHash,
        entity.WorkspaceHash,
        entity.RequestHash,
        Enum.Parse<CapabilityApprovalDisposition>(entity.Disposition, ignoreCase: false),
        Enum.Parse<CapabilityApprovalState>(entity.State, ignoreCase: false),
        new DateTimeOffset(entity.CreatedAtUtcTicks, TimeSpan.Zero),
        new DateTimeOffset(entity.ExpiresAtUtcTicks, TimeSpan.Zero),
        new ActorId(entity.DecidedBy),
        new CorrelationId(entity.CorrelationId),
        entity.PreviewHash,
        entity.IdempotencyKey,
        entity.Version,
        entity.ConsumedAtUtcTicks is null
            ? null
            : new DateTimeOffset(entity.ConsumedAtUtcTicks.Value, TimeSpan.Zero),
        entity.RevokedAtUtcTicks is null
            ? null
            : new DateTimeOffset(entity.RevokedAtUtcTicks.Value, TimeSpan.Zero));
}
