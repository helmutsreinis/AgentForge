using AgentForge.Domain.Primitives;
using AgentForge.Domain.Scheduling;

namespace AgentForge.Abstractions.Scheduling;

public interface IScheduledAgentRunStore
{
    ValueTask AddAsync(ScheduledAgentRunTemplate runTemplate, CancellationToken cancellationToken);

    ValueTask<ScheduledAgentRunTemplate?> FindAsync(
        ScheduleId scheduleId,
        CancellationToken cancellationToken);
}

public interface IScheduledAgentRunService
{
    Task<DomainResult<ScheduledAgentRunCreationResult>> CreateAsync(
        CreateScheduledAgentRunRequest request,
        CancellationToken cancellationToken);

    Task<DomainResult<ScheduledAgentRunExecutionResult>> ExecuteAsync(
        ScheduleSnapshot schedule,
        string occurrenceIdHash,
        CancellationToken cancellationToken);
}
