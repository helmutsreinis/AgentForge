using System.Text.Json;
using AgentForge.Abstractions.Scheduling;
using AgentForge.Domain.Scheduling;
using AgentForge.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace AgentForge.Persistence;

internal sealed class SqliteScheduledAgentRunStore(AgentForgeDbContext dbContext) : IScheduledAgentRunStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public async ValueTask AddAsync(
        ScheduledAgentRunTemplate runTemplate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runTemplate);
        if (!ScheduledAgentRunTemplateStateMachine.IsConsistent(runTemplate))
        {
            throw new ArgumentException("Only a self-consistent scheduled run template can be persisted.", nameof(runTemplate));
        }

        await dbContext.ScheduledAgentRunTemplates.AddAsync(new ScheduledAgentRunTemplateEntity
        {
            ScheduleId = runTemplate.ScheduleId.Value,
            InstallationId = runTemplate.InstallationId.Value,
            AgentId = runTemplate.AgentId.Value,
            ProviderId = runTemplate.ProviderId.Value,
            SystemInstructionArtifactHash = runTemplate.SystemInstructionArtifact.ContentHash,
            PromptArtifactHash = runTemplate.PromptArtifact.ContentHash,
            TemplateHash = runTemplate.TemplateHash,
            TemplateJson = JsonSerializer.Serialize(runTemplate, SerializerOptions),
            CreatedAtUtcTicks = runTemplate.CreatedAt.UtcTicks,
        }, cancellationToken);
    }

    public async ValueTask<ScheduledAgentRunTemplate?> FindAsync(
        ScheduleId scheduleId,
        CancellationToken cancellationToken)
    {
        var entity = await dbContext.ScheduledAgentRunTemplates.AsNoTracking()
            .SingleOrDefaultAsync(item => item.ScheduleId == scheduleId.Value, cancellationToken);
        if (entity is null) return null;

        var template = JsonSerializer.Deserialize<ScheduledAgentRunTemplate>(
            entity.TemplateJson, SerializerOptions)
            ?? throw new InvalidOperationException("The persisted scheduled run template was empty.");
        if (template.ScheduleId.Value != entity.ScheduleId ||
            template.InstallationId.Value != entity.InstallationId ||
            template.AgentId.Value != entity.AgentId || template.ProviderId.Value != entity.ProviderId ||
            !string.Equals(template.SystemInstructionArtifact.ContentHash,
                entity.SystemInstructionArtifactHash, StringComparison.Ordinal) ||
            !string.Equals(template.PromptArtifact.ContentHash, entity.PromptArtifactHash, StringComparison.Ordinal) ||
            !string.Equals(template.TemplateHash, entity.TemplateHash, StringComparison.Ordinal) ||
            template.CreatedAt.UtcTicks != entity.CreatedAtUtcTicks ||
            !ScheduledAgentRunTemplateStateMachine.IsConsistent(template))
        {
            throw new InvalidOperationException("The persisted scheduled run template failed integrity validation.");
        }

        return template;
    }
}
