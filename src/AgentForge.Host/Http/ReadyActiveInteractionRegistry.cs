using System.Collections.Concurrent;
using AgentForge.Domain.Orchestration;
using AgentForge.Domain.Primitives;

namespace AgentForge.Host.Http;

internal sealed record ReadyActiveInteraction(
    OrchestrationTaskId TaskId,
    InstallationId InstallationId,
    string SessionHash,
    CancellationTokenSource Cancellation)
{
    private readonly object _cancellationGate = new();
    private int _operatorCanceled;
    private bool _disposed;

    public bool OperatorCanceled => Volatile.Read(ref _operatorCanceled) != 0;

    public bool TryCancelByOperator()
    {
        lock (_cancellationGate)
        {
            if (_disposed)
            {
                return false;
            }

            Interlocked.Exchange(ref _operatorCanceled, 1);
            Cancellation.Cancel();
            return true;
        }
    }

    public void DisposeCancellation()
    {
        lock (_cancellationGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Cancellation.Dispose();
        }
    }
}

internal sealed class ReadyActiveInteractionRegistry
{
    private readonly ConcurrentDictionary<OrchestrationTaskId, ReadyActiveInteraction> _active = [];

    public bool TryAdd(ReadyActiveInteraction interaction) =>
        _active.TryAdd(interaction.TaskId, interaction);

    public bool TryCancel(
        OrchestrationTaskId taskId,
        InstallationId installationId,
        string sessionHash)
    {
        if (!_active.TryGetValue(taskId, out var interaction) ||
            interaction.InstallationId != installationId ||
            !string.Equals(interaction.SessionHash, sessionHash, StringComparison.Ordinal))
        {
            return false;
        }

        return interaction.TryCancelByOperator();
    }

    public bool WasCanceled(OrchestrationTaskId taskId) =>
        _active.TryGetValue(taskId, out var interaction) && interaction.OperatorCanceled;

    public void Remove(OrchestrationTaskId taskId)
    {
        if (_active.TryRemove(taskId, out var interaction))
        {
            interaction.DisposeCancellation();
        }
    }
}
