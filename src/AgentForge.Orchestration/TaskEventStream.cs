using System.Runtime.CompilerServices;
using AgentForge.Abstractions.Orchestration;
using AgentForge.Domain.Orchestration;

namespace AgentForge.Orchestration;

internal sealed class TaskEventStream(ITaskSnapshotStore snapshots, TimeProvider timeProvider) : IEventStream
{
    private const int MaximumEvents = 4096;

    public async IAsyncEnumerable<TaskProgressEvent> ReadTaskAsync(
        OrchestrationTaskId taskId,
        long afterVersion,
        bool follow,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (taskId.Value == Guid.Empty || afterVersion < -1)
            throw new ArgumentException("A task event stream requires a valid task and version cursor.");
        var deadline = timeProvider.GetUtcNow().AddMinutes(30);
        var emitted = 0;
        var cursor = afterVersion;
        while (emitted < MaximumEvents && timeProvider.GetUtcNow() < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var history = await snapshots.ListAsync(taskId, cancellationToken);
            foreach (var snapshot in history.Where(item => item.Version > cursor).OrderBy(item => item.Version))
            {
                if (snapshot.Version != cursor + 1)
                    throw new InvalidDataException("Task event history is non-contiguous.");
                cursor = snapshot.Version;
                emitted++;
                yield return new TaskProgressEvent(
                    taskId, snapshot.Version, snapshot.State, snapshot.SnapshotHash, snapshot.UpdatedAt);
                if (emitted >= MaximumEvents) yield break;
            }

            var latest = history.Count == 0 ? null : history[^1];
            if (!follow || latest is null || latest.State is OrchestrationTaskState.Completed or
                    OrchestrationTaskState.Failed or OrchestrationTaskState.Canceled or
                    OrchestrationTaskState.DeadLettered)
                yield break;
            await Task.Delay(TimeSpan.FromMilliseconds(250), timeProvider, cancellationToken);
        }
    }
}
