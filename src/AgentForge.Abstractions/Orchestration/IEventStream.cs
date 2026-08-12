using System.Diagnostics.CodeAnalysis;
using AgentForge.Domain.Orchestration;

namespace AgentForge.Abstractions.Orchestration;

public sealed record TaskProgressEvent(
    OrchestrationTaskId TaskId,
    long Version,
    OrchestrationTaskState State,
    string SnapshotHash,
    DateTimeOffset OccurredAt);

[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix",
    Justification = "IEventStream is a required stable AgentForge public contract.")]
public interface IEventStream
{
    IAsyncEnumerable<TaskProgressEvent> ReadTaskAsync(
        OrchestrationTaskId taskId,
        long afterVersion,
        bool follow,
        CancellationToken cancellationToken);
}
