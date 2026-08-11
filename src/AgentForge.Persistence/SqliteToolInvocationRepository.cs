using AgentForge.Abstractions.Tools;
using AgentForge.Domain.Agents;
using AgentForge.Domain.Primitives;
using AgentForge.Domain.Security;
using AgentForge.Domain.Tools;
using AgentForge.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace AgentForge.Persistence;

internal sealed class SqliteToolInvocationRepository(AgentForgeDbContext dbContext)
    : IToolInvocationRepository
{
    public async ValueTask AddAsync(ToolInvocationRecord invocation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        await dbContext.ToolInvocations.AddAsync(Map(invocation), cancellationToken);
    }

    public async ValueTask UpdateAsync(
        ToolInvocationRecord invocation,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        var entity = Map(invocation);
        var tracked = dbContext.ToolInvocations.Local.SingleOrDefault(item => item.Id == entity.Id);
        if (tracked is not null)
        {
            dbContext.Entry(tracked).CurrentValues.SetValues(entity);
            dbContext.Entry(tracked).Property(item => item.Version).OriginalValue = expectedVersion;
            await ValueTask.CompletedTask;
            return;
        }

        dbContext.Attach(entity);
        dbContext.Entry(entity).State = EntityState.Modified;
        dbContext.Entry(entity).Property(item => item.Version).OriginalValue = expectedVersion;
        await ValueTask.CompletedTask;
    }

    public async ValueTask<ToolInvocationRecord?> FindByIdempotencyKeyAsync(
        InstallationId installationId,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        var entity = await dbContext.ToolInvocations.AsNoTracking()
            .SingleOrDefaultAsync(item =>
                item.InstallationId == installationId.Value && item.IdempotencyKey == idempotencyKey,
                cancellationToken);
        return entity is null ? null : Map(entity);
    }

    private static ToolInvocationEntity Map(ToolInvocationRecord invocation) => new()
    {
        Id = invocation.Id.Value,
        InstallationId = invocation.InstallationId.Value,
        InstallationVersion = invocation.InstallationVersion,
        AgentId = invocation.AgentId.Value,
        AgentVersion = invocation.AgentVersion,
        ActorId = invocation.ActorId.Value,
        ToolId = invocation.ToolId,
        ToolVersion = invocation.ToolVersion,
        ToolDescriptorHash = invocation.ToolDescriptorHash,
        CapabilityId = invocation.CapabilityId,
        RiskClass = invocation.RiskClass.ToString(),
        ParametersHash = invocation.ParametersHash,
        TargetKind = invocation.TargetKind.ToString(),
        TargetHash = invocation.TargetHash,
        WorkspaceHash = invocation.WorkspaceHash,
        RequestHash = invocation.RequestHash,
        ApprovalId = invocation.ApprovalId?.Value,
        State = invocation.State.ToString(),
        CreatedAtUtcTicks = invocation.CreatedAt.UtcTicks,
        CompletedAtUtcTicks = invocation.CompletedAt?.UtcTicks,
        IdempotencyKey = invocation.IdempotencyKey,
        CorrelationId = invocation.CorrelationId.Value,
        CausationId = invocation.CausationId?.Value,
        ExitCode = invocation.ExitCode,
        StandardOutputHash = invocation.StandardOutputHash,
        StandardOutputLength = invocation.StandardOutputLength,
        StandardErrorHash = invocation.StandardErrorHash,
        StandardErrorLength = invocation.StandardErrorLength,
        FailureCode = invocation.FailureCode?.ToString(),
        Version = invocation.Version,
    };

    private static ToolInvocationRecord Map(ToolInvocationEntity entity) => new(
        new ToolInvocationId(entity.Id),
        new InstallationId(entity.InstallationId),
        entity.InstallationVersion,
        new AgentIdentityId(entity.AgentId),
        entity.AgentVersion,
        new ActorId(entity.ActorId),
        entity.ToolId,
        entity.ToolVersion,
        entity.ToolDescriptorHash,
        entity.CapabilityId,
        Enum.Parse<CapabilityRiskClass>(entity.RiskClass, ignoreCase: false),
        entity.ParametersHash,
        Enum.Parse<AuthorizationTargetKind>(entity.TargetKind, ignoreCase: false),
        entity.TargetHash,
        entity.WorkspaceHash,
        entity.RequestHash,
        entity.ApprovalId is null ? null : new CapabilityApprovalId(entity.ApprovalId.Value),
        Enum.Parse<ToolInvocationState>(entity.State, ignoreCase: false),
        new DateTimeOffset(entity.CreatedAtUtcTicks, TimeSpan.Zero),
        entity.CompletedAtUtcTicks is null
            ? null
            : new DateTimeOffset(entity.CompletedAtUtcTicks.Value, TimeSpan.Zero),
        entity.IdempotencyKey,
        new CorrelationId(entity.CorrelationId),
        entity.CausationId is null ? null : new CorrelationId(entity.CausationId),
        entity.ExitCode,
        entity.StandardOutputHash,
        entity.StandardOutputLength,
        entity.StandardErrorHash,
        entity.StandardErrorLength,
        entity.FailureCode is null ? null : Enum.Parse<FailureCode>(entity.FailureCode, ignoreCase: false),
        entity.Version);
}
